using System;
using System.Collections.Generic;
using System.Diagnostics;
using WindowsGSM.GameServer.Query;

namespace WindowsGSM.Functions
{
    public class ServerTable
    {
        public string ID { get; set; }
        public string PID { get; set; }
        public string Game { get; set; }
        public string Icon { get; set; }
        public string Status { get; set; }
        public string Name { get; set; }
        public string IP { get; set; }
        public string Port { get; set; }
        public string QueryPort { get; set; }
        public string Defaultmap { get; set; }
        public string Maxplayers { get; set; }
        public List<PlayerData> PlayerList { get; set; }
        public string Uptime { get; set; }

        // Live resource usage (populated by the UI refresh loop from the resource sampler).
        // Plain properties are fine here: the grid re-reads them on ServerGrid.Items.Refresh().
        public double CpuPercent { get; set; }
        public double MemoryMb { get; set; }

        /// <summary>True when the server is running and has a fresh resource sample.</summary>
        public bool HasResource { get; set; }

        /// <summary>CPU column text, e.g. "7%" — blank when not running.</summary>
        public string CpuDisplay => HasResource ? $"{Math.Round(CpuPercent)}%" : "—";

        /// <summary>Bar fill 0-100 for the CPU mini-bar.</summary>
        public double CpuBar => HasResource ? Math.Max(0, Math.Min(100, CpuPercent)) : 0;

        /// <summary>Pixel width of the filled portion of the CPU mini-bar (track is <see cref="BarTrackWidth"/>).</summary>
        public double CpuBarWidth => CpuBar / 100.0 * BarTrackWidth;

        /// <summary>RAM column text, e.g. "11.4G" / "820M" — blank when not running.</summary>
        public string RamDisplay
        {
            get
            {
                if (!HasResource) { return "—"; }
                return MemoryMb >= 1024
                    ? $"{MemoryMb / 1024.0:0.0}G"
                    : $"{Math.Round(MemoryMb)}M";
            }
        }

        /// <summary>Bar fill 0-100 for the RAM mini-bar, scaled against an 8 GB soft ceiling.</summary>
        public double RamBar => HasResource ? Math.Max(0, Math.Min(100, MemoryMb / 8192.0 * 100.0)) : 0;

        /// <summary>Pixel width of the filled portion of the RAM mini-bar (track is <see cref="BarTrackWidth"/>).</summary>
        public double RamBarWidth => RamBar / 100.0 * BarTrackWidth;

        /// <summary>Pixel width of the mini-bar track (shared by CPU/RAM columns in the server grid).</summary>
        public const double BarTrackWidth = 54;
    }
}
