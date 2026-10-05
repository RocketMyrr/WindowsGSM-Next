using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using WindowsGSM.Agent.Hosting;
using WindowsGSM.Contracts;

namespace WindowsGSM.Agent.Api;

/// <summary>
/// Copying a server here ("clone") and moving one to another machine. A move is a job on the agent you're using
/// (the hub): it asks the source machine to pack the server, carries the package across in pieces, and asks the
/// target machine to unpack it as a new server — every step through the normal API as you, so the usual
/// permissions apply on both machines (and the hub only relays; members never need to reach each other).
/// </summary>
public static class TransferEndpoints
{
    public const int ChunkBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);

    public sealed record CloneRequest(string Name);
    public sealed record ImportStart(string Name, long Size, string Sha256);
    public sealed record MoveRequest(string SourceMachine, string ServerId, string TargetMachine, string? Name, bool DeleteOriginal);

    private static string Transfers => Path.Combine(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot, "cache", "transfers");
    private static bool ValidToken(string t) => t.Length == 32 && t.All(Uri.IsHexDigit);
    // The server id is part of the name: a package can only be read through the server it was made from.
    private static string ExportFile(string server, string token) => Path.Combine(Transfers, $"export-{server}-{token}.zip");
    private static string ImportFile(string token) => Path.Combine(Transfers, $"import-{token}.zip");

    /// <summary>Packages nobody finished with (a move that stopped half-way) go after a day.</summary>
    private static void Tidy()
    {
        try
        {
            if (!Directory.Exists(Transfers)) { return; }
            foreach (string f in Directory.EnumerateFiles(Transfers).Where(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > KeepFor)) { try { File.Delete(f); } catch { } }
        }
        catch { /* next time */ }
    }

    public static void Map(RouteGroupBuilder api, RouteGroupBuilder server)
    {
        // ── Clone (this machine) ──
        server.MapPost("/clone", (HttpContext http, AgentContext ctx, CloneRequest body) =>
        {
            var s = Scopes.Server(http);
            if (!Scopes.User(http).CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden("Copying a server needs the Install permission on this machine."); }
            var request = ctx.Engine.Provisioning.Clone(s.Id, body.Name ?? "");
            ctx.Record(http, "clone", s.Id, request.Accepted, request.Accepted ? $"as \"{body.Name}\"" : request.Error);
            return ApiResults.FromRequest(request, ctx);
        }).Needs(Capability.Files);

        // ── Source side: pack, then hand out the package in pieces ──
        server.MapPost("/export", (HttpContext http, AgentContext ctx) =>
        {
            var s = Scopes.Server(http);
            Tidy();
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var request = ctx.Engine.Provisioning.Export(s.Id, ExportFile(s.Id, token));
            ctx.Record(http, "export", s.Id, request.Accepted, request.Error);
            if (!request.Accepted || request.Job == null) { return ApiResults.FromRequest(request, ctx); }
            return Results.Json(new { token, job = ctx.ToDto(request.Job.Snapshot()) }, statusCode: 202);
        }).Needs(Capability.Files);

        server.MapGet("/export/{token}", (HttpContext http, string token) =>
        {
            if (!ValidToken(token) || !File.Exists(ExportFile(Scopes.Server(http).Id, token))) { return ApiResults.NotFound("No such package."); }
            using var fs = File.OpenRead(ExportFile(Scopes.Server(http).Id, token));
            return Results.Json(new { size = fs.Length, sha256 = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant() });
        }).Needs(Capability.Files);

        server.MapGet("/export/{token}/data", async (HttpContext http, string token, long offset, int? length) =>
        {
            if (!ValidToken(token) || !File.Exists(ExportFile(Scopes.Server(http).Id, token))) { return ApiResults.NotFound("No such package."); }
            int want = Math.Clamp(length ?? ChunkBytes, 1, ChunkBytes);
            await using var fs = File.OpenRead(ExportFile(Scopes.Server(http).Id, token));
            if (offset < 0 || offset > fs.Length) { return ApiResults.BadRequest("Bad offset."); }
            fs.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(want, fs.Length - offset)];
            int read = 0;
            while (read < buffer.Length) { int n = await fs.ReadAsync(buffer.AsMemory(read), http.RequestAborted); if (n == 0) { break; } read += n; }
            return Results.Bytes(buffer.AsMemory(0, read).ToArray(), "application/octet-stream");
        }).Needs(Capability.Files);

        server.MapDelete("/export/{token}", (HttpContext http, string token) =>
        {
            if (ValidToken(token)) { try { File.Delete(ExportFile(Scopes.Server(http).Id, token)); } catch { } }
            return Results.NoContent();
        }).Needs(Capability.Files);

        // ── Target side: receive the pieces, then unpack as a new server ──
        var imports = api.MapGroup("/machines/{machine}/imports").RequireMachine();

        imports.MapPost("", (HttpContext http, AgentContext ctx, ImportStart body) =>
        {
            if (!ctx.CurrentUser(http)!.CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden("Moving a server here needs the Install permission on this machine."); }
            if (body.Size <= 0 || string.IsNullOrWhiteSpace(body.Sha256)) { return ApiResults.BadRequest("Missing the package's size or checksum."); }
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(global::WindowsGSM.Hosting.WgsmEnvironment.DataRoot))!);
                if (drive.AvailableFreeSpace < body.Size * 2 + 1024L * 1024 * 1024) { return ApiResults.BadRequest($"Not enough free space here: the server needs about {body.Size * 2 / 1073741824.0:0.#} GB (package + unpacked)."); }
            }
            catch { /* can't tell — try */ }
            Tidy();
            Directory.CreateDirectory(Transfers);
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            File.WriteAllText(ImportFile(token) + ".json", JsonSerializer.Serialize(body));
            File.Create(ImportFile(token)).Dispose();
            return Results.Json(new { token });
        });

        imports.MapPut("/{token}", async (HttpContext http, AgentContext ctx, string token, long offset) =>
        {
            if (!ctx.CurrentUser(http)!.CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden(); }
            if (!ValidToken(token) || !File.Exists(ImportFile(token))) { return ApiResults.NotFound("No such upload."); }
            var size = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) { size.MaxRequestBodySize = ChunkBytes + 1024; }
            await using var fs = new FileStream(ImportFile(token), FileMode.Open, FileAccess.Write, FileShare.None);
            if (offset != fs.Length) { return ApiResults.Conflict($"Expected the piece at {fs.Length}."); }
            fs.Seek(offset, SeekOrigin.Begin);
            await http.Request.Body.CopyToAsync(fs, http.RequestAborted);
            return Results.Json(new { received = fs.Length });
        }).DisableAntiforgery();

        imports.MapPost("/{token}/finish", async (HttpContext http, AgentContext ctx, string token) =>
        {
            if (!ctx.CurrentUser(http)!.CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden(); }
            if (!ValidToken(token) || !File.Exists(ImportFile(token)) || !File.Exists(ImportFile(token) + ".json")) { return ApiResults.NotFound("No such upload."); }
            var start = JsonSerializer.Deserialize<ImportStart>(await File.ReadAllTextAsync(ImportFile(token) + ".json"))!;
            await using (var fs = File.OpenRead(ImportFile(token)))
            {
                if (fs.Length != start.Size) { return ApiResults.BadRequest($"Only {fs.Length} of {start.Size} bytes arrived."); }
                string sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (!string.Equals(sha, start.Sha256, StringComparison.OrdinalIgnoreCase)) { return ApiResults.BadRequest("The package arrived damaged (checksum mismatch)."); }
            }
            var request = ctx.Engine.Provisioning.ImportPackage(ImportFile(token), start.Name);
            ctx.Record(http, "import", null, request.Accepted, request.Accepted ? $"moved server \"{start.Name}\"" : request.Error);
            // The package goes once it's unpacked (or failed).
            if (request.Job != null)
            {
                _ = request.Job.Completion.ContinueWith(_ => { try { File.Delete(ImportFile(token)); File.Delete(ImportFile(token) + ".json"); } catch { } }, TaskScheduler.Default);
            }
            return ApiResults.FromRequest(request, ctx);
        });

        imports.MapDelete("/{token}", (HttpContext http, AgentContext ctx, string token) =>
        {
            if (!ctx.CurrentUser(http)!.CanOnMachine(Capability.Install, ctx.MachineId)) { return ApiResults.Forbidden(); }
            if (ValidToken(token)) { try { File.Delete(ImportFile(token)); File.Delete(ImportFile(token) + ".json"); } catch { } }
            return Results.NoContent();
        });

        // ── The move itself (runs on this agent; machines are reached through the API) ──
        api.MapPost("/move", (HttpContext http, AgentContext ctx, LocalApi local, MoveRequest body) =>
        {
            var user = ctx.CurrentUser(http);
            if (user == null) { return ApiResults.Unauthorized(); }
            if (string.Equals(body.SourceMachine, body.TargetMachine, StringComparison.OrdinalIgnoreCase)) { return ApiResults.BadRequest("That's the same machine — use Copy instead."); }
            var job = ctx.Engine.Jobs.Start("move", null, $"Move server #{body.ServerId} to another machine", jc => MoveAsync(local, user, body, jc));
            ctx.Record(http, "move", body.ServerId, true, $"{body.SourceMachine} → {body.TargetMachine}");
            return Results.Json(new JobAccepted(job.Id, ctx.ToDto(job.Snapshot())), statusCode: 202);
        });
    }

    private static async Task<string?> MoveAsync(LocalApi local, Security.AgentUser user, MoveRequest m, WindowsGSM.Engine.Operations.JobContext job)
    {
        using var http = local.Client();
        http.Timeout = TimeSpan.FromMinutes(5);
        string src = $"/machines/{Uri.EscapeDataString(m.SourceMachine)}/servers/{Uri.EscapeDataString(m.ServerId)}";
        string dst = $"/machines/{Uri.EscapeDataString(m.TargetMachine)}";

        async Task<JsonElement> Call(HttpMethod method, string path, object? json = null, HttpContent? content = null)
        {
            using var req = local.Request(method, path, user, "move", json);
            if (content != null) { req.Content = content; }
            using var res = await http.SendAsync(req, job.Cancellation);
            string text = await res.Content.ReadAsStringAsync(job.Cancellation);
            if (!res.IsSuccessStatusCode)
            {
                string why = text;
                try { using var d = JsonDocument.Parse(text); if (d.RootElement.TryGetProperty("error", out var e)) { why = e.GetString() ?? text; } } catch { }
                throw new InvalidOperationException(why.Length > 0 ? why : $"{(int)res.StatusCode} {res.ReasonPhrase}");
            }
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        async Task<string?> Wait(string machine, string jobId, string stage, int from, int to)
        {
            while (true)
            {
                await Task.Delay(1000, job.Cancellation);
                var j = await Call(HttpMethod.Get, $"/machines/{Uri.EscapeDataString(machine)}/jobs/{Uri.EscapeDataString(jobId)}");
                string status = j.GetProperty("status").GetString() ?? "";
                if (j.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number) { job.Report(from + (to - from) * p.GetInt32() / 100, stage); }
                if (status == "Running") { continue; }
                return status == "Succeeded" ? null : (j.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : status);
            }
        }

        string? token = null, upload = null;
        try
        {
            var server = await Call(HttpMethod.Get, src);
            string name = string.IsNullOrWhiteSpace(m.Name) ? server.GetProperty("name").GetString() ?? "Moved server" : m.Name!.Trim();

            job.Report(0, "Packing on the source machine");
            var packed = await Call(HttpMethod.Post, src + "/export");
            token = packed.GetProperty("token").GetString()!;
            if (await Wait(m.SourceMachine, packed.GetProperty("job").GetProperty("id").GetString()!, "Packing on the source machine", 0, 30) is { } packError) { return packError; }

            var info = await Call(HttpMethod.Get, $"{src}/export/{token}");
            long size = info.GetProperty("size").GetInt64();
            string sha = info.GetProperty("sha256").GetString()!;
            upload = (await Call(HttpMethod.Post, dst + "/imports", new ImportStart(name, size, sha))).GetProperty("token").GetString()!;

            for (long offset = 0; offset < size;)
            {
                job.Cancellation.ThrowIfCancellationRequested();
                using var req = local.Request(HttpMethod.Get, $"{src}/export/{token}/data?offset={offset}&length={ChunkBytes}", user, "move");
                using var res = await http.SendAsync(req, job.Cancellation);
                res.EnsureSuccessStatusCode();
                byte[] piece = await res.Content.ReadAsByteArrayAsync(job.Cancellation);
                if (piece.Length == 0) { return "The source machine stopped sending the package."; }
                await Call(HttpMethod.Put, $"{dst}/imports/{upload}?offset={offset}", content: new ByteArrayContent(piece));
                offset += piece.Length;
                job.Report(30 + (int)(offset * 50 / size), $"Sending {offset / 1048576} of {size / 1048576} MB");
            }

            var unpacking = await Call(HttpMethod.Post, $"{dst}/imports/{upload}/finish");
            upload = null; // the target cleans it up from here
            if (await Wait(m.TargetMachine, unpacking.GetProperty("jobId").GetString()!, "Unpacking on the target machine", 80, 99) is { } unpackError) { return unpackError; }

            if (m.DeleteOriginal)
            {
                job.Report(99, "Deleting the original");
                var del = await Call(HttpMethod.Delete, src);
                if (await Wait(m.SourceMachine, del.GetProperty("jobId").GetString()!, "Deleting the original", 99, 100) is { } delError)
                {
                    job.Log("Moved, but the original couldn't be deleted: " + delError);
                }
            }
            job.Log($"\"{name}\" is now on the target machine.");
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or KeyNotFoundException or JsonException)
        {
            return ex.Message;
        }
        finally
        {
            if (token != null) { try { await Call(HttpMethod.Delete, $"{src}/export/{token}"); } catch { } }
            if (upload != null) { try { await Call(HttpMethod.Delete, $"{dst}/imports/{upload}"); } catch { } }
        }
    }
}
