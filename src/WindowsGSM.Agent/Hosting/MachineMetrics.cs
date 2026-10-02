using WindowsGSM.Contracts;
using WindowsGSM.Functions;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// This machine's CPU / RAM / disk. Each reading is a couple of WMI queries, so one sample is shared by every
/// viewer for a few seconds (and the lock stops simultaneous requests each running their own queries).
/// </summary>
public sealed class MachineMetrics
{
    private static readonly TimeSpan SampleFor = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly SystemMetrics? _metrics;
    private HostMetricsDto? _last;

    public MachineMetrics()
    {
        try
        {
            var m = new SystemMetrics();
            m.GetCPUStaticInfo();
            m.GetRAMStaticInfo();
            m.GetDiskStaticInfo();
            _metrics = m;
        }
        catch { _metrics = null; } // metrics are best-effort; the API returns null
    }

    public HostMetricsDto? Sample()
    {
        if (_metrics == null) { return null; }
        lock (_gate)
        {
            if (_last != null && DateTimeOffset.UtcNow - _last.At < SampleFor) { return _last; }
            try
            {
                _last = new HostMetricsDto(
                    Math.Round(_metrics.GetCPUUsage(), 0),
                    Math.Round(_metrics.GetRAMUsage(), 0),
                    Math.Round(_metrics.RAMTotalSize / 1024.0 / 1024.0, 1),            // RAMTotalSize is KB
                    Math.Round(_metrics.GetDiskUsage(), 0),
                    Math.Round(_metrics.DiskTotalSize / 1024.0 / 1024.0 / 1024.0, 0),  // DiskTotalSize is bytes
                    _metrics.CPUCoreCount,
                    _metrics.CPUType,
                    DateTimeOffset.UtcNow);
            }
            catch { /* keep the previous sample */ }
            return _last;
        }
    }
}
