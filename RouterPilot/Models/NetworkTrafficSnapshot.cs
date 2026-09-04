using System;

namespace RouterPilot.Models
{
    public class NetworkTrafficSnapshot
    {
        public string InterfaceName { get; set; } = "-";

        public long ReceivedBytes { get; set; }

        public long TransmittedBytes { get; set; }

        public bool IsValid { get; set; }

        public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
