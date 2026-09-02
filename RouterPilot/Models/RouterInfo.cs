using System.Collections.Generic;

namespace RouterPilot.Models
{
    public class RouterInfo
    {
        public string Model { get; set; } = "-";

        public string Hostname { get; set; } = "-";

        public string Firmware { get; set; } = "-";

        public string Uptime { get; set; } = "-";

        public string CpuUsage { get; set; } = "-";

        public double? CpuUsagePercent { get; set; }

        public bool CpuUtilisationPending { get; set; }

        public double? LoadAverage1Minute { get; set; }

        public int? LogicalProcessorCount { get; set; }

        public string Temperature { get; set; } = "-";

        public string LoadAverage { get; set; } = "-";

        public string MemoryUsage { get; set; } = "-";

        public string MemoryUsed { get; set; } = "-";

        public string MemoryCache { get; set; } = "-";

        public string StorageUsage { get; set; } = "-";
        public bool ExternalStorageInventoryLoaded { get; set; }
        public List<MountedStorageInfo> ExternalStorage { get; set; } = new();
        public List<StorageDeviceInfo> AttachedStorage { get; set; } = new();
        public bool FileSharingInventoryLoaded { get; set; }
        public List<SambaShareInfo> SambaShares { get; set; } = new();


        //
        // Backwards compatibility
        // DiagnosticsWindow currently expects these
        //

        public string WanIp { get; set; } = "-";

        public string Gateway { get; set; } = "-";

        public string DnsServer { get; set; } = "-";

        public string Latency { get; set; } = "-";
    }

    public sealed class MountedStorageInfo
    {
        public string Device { get; set; } = "-";
        public string MountPoint { get; set; } = "-";
        public string FileSystem { get; set; } = "-";
        public string Capacity { get; set; } = "-";
        public string Used { get; set; } = "-";
        public string Available { get; set; } = "-";
        public string Usage { get; set; } = "-";
        public bool ReadOnly { get; set; }
    }

    public sealed class StorageDeviceInfo
    {
        public string Device { get; set; } = "-";
        public string Size { get; set; } = "-";
        public bool Removable { get; set; }
    }

    public sealed class SambaShareInfo
    {
        public string Name { get; set; } = "Unknown share";
        public string Path { get; set; } = string.Empty;
        public bool? GuestAccess { get; set; }
        public bool? ReadOnly { get; set; }
        public bool? Enabled { get; set; }
        public string StorageDisplay { get; set; } = "Unknown storage";
    }
}
