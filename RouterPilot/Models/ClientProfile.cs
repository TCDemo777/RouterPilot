using System;

namespace RouterPilot.Models
{
    public class ClientProfile
    {
        public string Key { get; set; } = string.Empty;
        public string Nickname { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public bool IsFavorite { get; set; }
        public bool IsKnown { get; set; } = true;
        public bool NeedsReview { get; set; }
        public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public string LastKnownName { get; set; } = string.Empty;
        public string LastKnownIpAddress { get; set; } = string.Empty;
        public string LastKnownConnectionSummary { get; set; } = string.Empty;
    }
}
