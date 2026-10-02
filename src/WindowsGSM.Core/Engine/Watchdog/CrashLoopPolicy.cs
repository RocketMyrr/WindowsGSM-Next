#nullable enable
using System;
using System.Collections.Generic;

namespace WindowsGSM.Engine.Watchdog
{
    /// <summary>Crash-loop tuning. Defaults are the legacy app's values.</summary>
    public sealed class CrashLoopOptions
    {
        /// <summary>Rolling window in which crashes are counted.</summary>
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Crashes in the window before auto-restart starts backing off.</summary>
        public int SoftThreshold { get; set; } = 3;

        /// <summary>Crashes in the window before auto-restart gives up.</summary>
        public int HardThreshold { get; set; } = 6;

        public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(15);
        public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(300);
    }

    public enum CrashResponse { RestartNow, RestartAfterDelay, Suspend }

    public readonly record struct CrashDecision(CrashResponse Response, TimeSpan Delay, int CrashesInWindow);

    /// <summary>
    /// What auto-restart should do after a crash. Ported unchanged from the legacy watchdog: count crashes
    /// in a rolling window; beyond the soft threshold wait with exponential backoff; at the hard threshold
    /// stop trying, because a server that keeps crashing won't fix itself.
    /// </summary>
    public static class CrashLoopPolicy
    {
        /// <summary>Records a crash at <paramref name="now"/> in <paramref name="recentCrashes"/> (pruning old ones) and decides.</summary>
        public static CrashDecision OnCrash(List<DateTimeOffset> recentCrashes, DateTimeOffset now, CrashLoopOptions options)
        {
            recentCrashes.Add(now);
            recentCrashes.RemoveAll(t => now - t > options.Window);
            int count = recentCrashes.Count;

            if (count >= options.HardThreshold)
            {
                return new CrashDecision(CrashResponse.Suspend, TimeSpan.Zero, count);
            }

            if (count > options.SoftThreshold)
            {
                int over = count - options.SoftThreshold;
                double seconds = Math.Min(options.BackoffBase.TotalSeconds * Math.Pow(2, over - 1), options.BackoffMax.TotalSeconds);
                return new CrashDecision(CrashResponse.RestartAfterDelay, TimeSpan.FromSeconds(seconds), count);
            }

            return new CrashDecision(CrashResponse.RestartNow, TimeSpan.Zero, count);
        }
    }
}
