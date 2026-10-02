using System.Collections.Concurrent;
using System.Text.Json;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// Structured agent log: one JSON object per line in logs/agent/agent-yyyyMMdd.jsonl (time, level, category,
/// message, exception). Easy to grep, easy to ship. Game-server activity keeps going to the legacy-format
/// daily logs; this is the agent's own diagnostics. Files older than 14 days are removed.
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public JsonFileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "wgsm-agent-log" };
        _writer.Start();
        try
        {
            foreach (string old in Directory.GetFiles(dir, "agent-*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)))
            {
                File.Delete(old);
            }
        }
        catch { /* housekeeping only */ }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line); // drop rather than block when overwhelmed

    private void WriteLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try { File.AppendAllText(Path.Combine(_dir, $"agent-{DateTime.Now:yyyyMMdd}.jsonl"), line + Environment.NewLine); }
            catch { /* logging must never take the agent down */ }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class Logger : ILogger
    {
        private readonly JsonFileLoggerProvider _owner;
        private readonly string _category;
        public Logger(JsonFileLoggerProvider owner, string category) { _owner = owner; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (_category.StartsWith("Microsoft.", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) { return; }
            _owner.Enqueue(JsonSerializer.Serialize(new
            {
                t = DateTimeOffset.Now,
                level = logLevel.ToString(),
                category = _category,
                message = formatter(state, exception),
                exception = exception?.ToString(),
            }));
        }
    }
}
