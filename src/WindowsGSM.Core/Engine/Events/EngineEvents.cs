#nullable enable
using System;
using WindowsGSM.Engine.Operations;
using WindowsGSM.Engine.Servers;

namespace WindowsGSM.Engine.Events
{
    /// <summary>Base for every engine event. <see cref="At"/> is UTC.</summary>
    public abstract record EngineEvent
    {
        public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>A server moved between states (Stopped → Starting → Running …).</summary>
    public sealed record ServerStateChanged(string ServerId, ServerState From, ServerState To) : EngineEvent;

    /// <summary>A line in WindowsGSM's own log for a server (or "System"), same text as the daily log file.</summary>
    public sealed record ServerLogged(string ServerId, LogLevel Level, string Message) : EngineEvent;

    /// <summary>A line of the game server's own console output (embedded console).</summary>
    public sealed record ConsoleLineAdded(string ServerId, string Line) : EngineEvent;

    /// <summary>A job started, progressed or finished.</summary>
    public sealed record JobChanged(JobSnapshot Job) : EngineEvent;

    /// <summary>Something a person should know about — crashes, suspended auto-restart, memory guard, join codes.</summary>
    public sealed record ServerAlert(string ServerId, AlertKind Kind, string Title, string Text) : EngineEvent;

    /// <summary>A server was added to or removed from the registry.</summary>
    public sealed record ServerListChanged(string ServerId, bool Removed) : EngineEvent;

    /// <summary>A server's saved settings changed (names, ports, toggles…). <see cref="Keys"/> lists what changed.</summary>
    public sealed record ServerConfigChanged(string ServerId, System.Collections.Generic.IReadOnlyList<string> Keys) : EngineEvent;

    public enum LogLevel { Info, Notice, Error }

    public enum AlertKind { Crashed, AutoRestarted, CrashLoopSuspended, MemoryGuard, JoinCode, AutoStarted, ScheduledRestart, AutoUpdated, Other }
}
