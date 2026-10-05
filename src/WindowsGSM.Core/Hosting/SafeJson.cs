#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

// Also compiled into the desktop app (its own copy, in its own namespace).
#if WGSM_DESKTOP
namespace WindowsGSM.Desktop.Shared
#else
namespace WindowsGSM.Hosting
#endif
{
    /// <summary>
    /// How WindowsGSM reads and writes its own settings files (users, schedules, automations, hub machines…),
    /// so that a file it can't read never turns into lost settings:
    /// <list type="bullet">
    /// <item>Every save is atomic and keeps the previous good copy as <c>&lt;file&gt;.bak</c>.</item>
    /// <item>A damaged file is read from that copy instead. If both are unreadable, the damaged file is kept aside as
    /// <c>&lt;file&gt;.unreadable-&lt;time&gt;</c> — the store starts empty, but the next save can't overwrite what
    /// was there — and <see cref="Problems"/> (shown in Health checks) says so.</item>
    /// <item>Reading is forgiving: properties and option values this version doesn't know (written by a newer one —
    /// e.g. after Updates → Go back) are ignored rather than failing the whole file, and hand edits may use comments,
    /// trailing commas and numbers in quotes.</item>
    /// </list>
    /// </summary>
    public static class SafeJson
    {
        /// <summary>A settings file that couldn't be read: (file, what happened, where the original was kept).</summary>
        public sealed record Problem(string File, string Message, string? KeptAs, DateTimeOffset At, bool Recovered);

        /// <summary>True when the last read of <paramref name="file"/> found it unreadable with no previous copy to use.</summary>
        public static bool Lost(string file) => _problems.TryGetValue(file, out var p) && !p.Recovered;

        private static readonly ConcurrentDictionary<string, Problem> _problems = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files that couldn't be read since the agent started (newest problem per file).</summary>
        public static IReadOnlyList<Problem> Problems => _problems.Values.OrderBy(p => p.At).ToList();

        /// <summary>Told about every problem (the agent writes it to its log).</summary>
        public static Action<string>? Log { get; set; }

        private static readonly JsonSerializerOptions Plain = new();

        /// <summary>
        /// Reads <paramref name="file"/> (or its .bak) — default(T) when neither exists. Never throws for a bad file;
        /// see the class summary.
        /// </summary>
        public static T? Read<T>(string file, JsonSerializerOptions? options = null)
        {
            var lenient = Lenient(options);
            return ReadWith(file, text => JsonSerializer.Deserialize<T>(text, lenient));
        }

        /// <summary>
        /// The same for a file read some other way (Newtonsoft, a legacy format…): <paramref name="parse"/> turns the
        /// text into the value and throws when it can't.
        /// </summary>
        public static T? ReadWith<T>(string file, Func<string, T?> parse)
        {
            string backup = file + ".bak";
            if (!File.Exists(file))
            {
                // Saved as far as the .tmp → .bak swap, then stopped: the .bak is the latest good copy.
                return File.Exists(backup) && TryRead(backup, parse, out var fromBackup, out _) ? fromBackup : default;
            }
            if (TryRead(file, parse, out var value, out string? error)) { _problems.TryRemove(file, out _); return value; }

            if (File.Exists(backup) && TryRead(backup, parse, out var previous, out _))
            {
                string? kept = KeepAside(file);
                Report(new Problem(file, $"couldn't be read ({error}) — the previous copy was used instead", kept, DateTimeOffset.Now, Recovered: true));
                return previous;
            }
            string? keptAs = KeepAside(file);
            Report(new Problem(file, $"couldn't be read ({error}) and has no readable previous copy — it starts empty", keptAs, DateTimeOffset.Now, Recovered: false));
            return default;
        }

        /// <summary>
        /// Writes <paramref name="value"/> atomically, keeping the file it replaces as <c>&lt;file&gt;.bak</c>.
        /// Serializes first, so a value that can't be written never touches the files.
        /// </summary>
        public static void Write<T>(string file, T value, JsonSerializerOptions? options = null) =>
            WriteText(file, JsonSerializer.SerializeToUtf8Bytes(value, options ?? Plain));

        /// <summary>The same for text produced some other way (Newtonsoft…).</summary>
        public static void WriteText(string file, string text) => WriteText(file, new System.Text.UTF8Encoding(false).GetBytes(text));

        private static void WriteText(string file, byte[] bytes)
        {
            string? dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) { Directory.CreateDirectory(dir); }
            string temp = file + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (File.Exists(file))
            {
                // Only a file that reads becomes the .bak — a damaged one would replace the good previous copy.
                if (IsReadableJson(file)) { File.Replace(temp, file, file + ".bak", ignoreMetadataErrors: true); }
                else { File.Move(temp, file, overwrite: true); }
            }
            else { File.Move(temp, file); }
            _problems.TryRemove(file, out _);
        }

        // ── Internals ──

        private static bool TryRead<T>(string file, Func<string, T?> parse, out T? value, out string? error)
        {
            try
            {
                string text = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(text)) { throw new JsonException("the file is empty"); }
                value = parse(text);
                if (value == null && typeof(T).IsClass && text.Trim() != "null") { throw new JsonException("no value in the file"); }
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                value = default;
                error = ex.Message.Split('\n')[0].Trim();
                return false;
            }
        }

        private static bool IsReadableJson(string file)
        {
            try { using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); return true; }
            catch { return false; }
        }

        /// <summary>Copies the unreadable file aside (once per content) so nothing can overwrite it; returns the copy.</summary>
        private static string? KeepAside(string file)
        {
            try
            {
                string dir = Path.GetDirectoryName(file) ?? ".";
                long length = new FileInfo(file).Length;
                // Already kept (the agent restarted with the same bad file): don't pile up copies.
                foreach (string existing in Directory.EnumerateFiles(dir, Path.GetFileName(file) + ".unreadable-*"))
                {
                    if (new FileInfo(existing).Length == length && File.ReadAllBytes(existing).AsSpan().SequenceEqual(File.ReadAllBytes(file))) { return existing; }
                }
                string copy = $"{file}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(file, copy, overwrite: false);
                return copy;
            }
            catch { return null; }
        }

        private static void Report(Problem p)
        {
            _problems[p.File] = p;
            try { Log?.Invoke($"{Path.GetFileName(p.File)} {p.Message}" + (p.KeptAs != null ? $". The original is kept as {Path.GetFileName(p.KeptAs)}." : ".")); } catch { }
        }

        /// <summary>
        /// Newtonsoft settings that skip what can't be read (an option value a newer version added, a mistyped
        /// field) instead of failing the whole file.
        /// </summary>
#if !WGSM_DESKTOP
        public static Newtonsoft.Json.JsonSerializerSettings LenientNewtonsoft(Newtonsoft.Json.JsonSerializerSettings? settings = null)
        {
            var s = settings == null ? new Newtonsoft.Json.JsonSerializerSettings() : new Newtonsoft.Json.JsonSerializerSettings
            {
                Converters = settings.Converters, ContractResolver = settings.ContractResolver, DateParseHandling = settings.DateParseHandling,
                NullValueHandling = settings.NullValueHandling, DefaultValueHandling = settings.DefaultValueHandling, ObjectCreationHandling = settings.ObjectCreationHandling,
            };
            s.MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore;
            // Only values that don't fit: broken JSON (a cut-off file) still fails, so the .bak is used instead of
            // half a file.
            s.Error = (_, e) => { if (e.ErrorContext.Error is not Newtonsoft.Json.JsonReaderException) { e.ErrorContext.Handled = true; } };
            return s;
        }
#endif

        private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonSerializerOptions> _lenient = new();

        /// <summary>The same options, forgiving for reading: see the class summary.</summary>
        public static JsonSerializerOptions Lenient(JsonSerializerOptions? options) =>
            _lenient.GetOrAdd(options ?? Plain, o =>
            {
                var l = new JsonSerializerOptions(o)
                {
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    NumberHandling = o.NumberHandling | JsonNumberHandling.AllowReadingFromString,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
                };
                // Enum values a newer version added read as the default instead of failing the whole file.
                bool strings = false;
                for (int i = l.Converters.Count - 1; i >= 0; i--)
                {
                    if (l.Converters[i] is JsonStringEnumConverter) { l.Converters.RemoveAt(i); strings = true; }
                }
                l.Converters.Add(new LenientEnumConverterFactory(strings));
                return l;
            });

        /// <summary>Reads enums as names or numbers; anything unknown becomes the default value. Writes as before.</summary>
        private sealed class LenientEnumConverterFactory : JsonConverterFactory
        {
            private readonly bool _asStrings;
            public LenientEnumConverterFactory(bool asStrings) => _asStrings = asStrings;
            public override bool CanConvert(Type t) => t.IsEnum;
            public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
                (JsonConverter)Activator.CreateInstance(typeof(LenientEnum<>).MakeGenericType(t), new object[] { _asStrings })!;
        }

        private sealed class LenientEnum<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
        {
            private readonly bool _asStrings;
            public LenientEnum(bool asStrings) => _asStrings = asStrings;

            public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    string? s = reader.GetString();
                    return Enum.TryParse<TEnum>(s, ignoreCase: true, out var v) && (Enum.IsDefined(v) || IsFlags) ? v : default;
                }
                if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long n))
                {
                    var v = (TEnum)Enum.ToObject(typeof(TEnum), n);
                    return Enum.IsDefined(v) || IsFlags ? v : default;
                }
                reader.Skip();
                return default;
            }

            public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            {
                if (_asStrings) { writer.WriteStringValue(value.ToString()); }
                else { writer.WriteNumberValue(Convert.ToInt64(value)); }
            }

            private static bool IsFlags => typeof(TEnum).GetCustomAttribute<FlagsAttribute>() != null;
        }
    }
}
