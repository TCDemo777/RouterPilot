using System;

namespace RouterPilot.Models
{
    public class ClientInfo
    {
        public string Name { get; set; } = "-";
        public string RouterName { get; set; } = "-";
        public string Notes { get; set; } = string.Empty;
        public string CustomCategory { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastObservedUtc { get; set; }
        public string IpAddress { get; set; } = "-";
        public string MacAddress { get; set; } = "-";
        public int TotalQueries { get; set; }
        public int BlockedQueries { get; set; }
        public AdGuardAvailabilityState AdGuardDataAvailability { get; set; } =
            AdGuardAvailabilityState.Unavailable;

        public double BlockRate =>
            TotalQueries == 0
                ? 0
                : (double)BlockedQueries / TotalQueries * 100;

        public string LastSeen { get; set; } = "-";

        // Set by RouterManager when AdGuard Home query logging is available.
        // Query totals may still be populated from /control/stats when false.
        public bool QueryLogAvailable { get; set; } = true;

        public string LastSeenDisplay =>
            AdGuardDataAvailability != AdGuardAvailabilityState.Available
                ? RouterPilotStatusPresentation.NotAvailable
                : QueryLogAvailable
                ? LastSeen
                : "Query log disabled";

        public string TotalQueriesDisplay =>
            AdGuardDataAvailability == AdGuardAvailabilityState.Available
                ? TotalQueries.ToString("N0")
                : RouterPilotStatusPresentation.NotAvailable;

        public string BlockedQueriesDisplay =>
            AdGuardDataAvailability == AdGuardAvailabilityState.Available
                ? BlockedQueries.ToString("N0")
                : RouterPilotStatusPresentation.NotAvailable;

        public string BlockRateDisplay =>
            AdGuardDataAvailability == AdGuardAvailabilityState.Available
                ? $"{BlockRate:F1}%"
                : RouterPilotStatusPresentation.NotAvailable;

        public string ActivityAvailabilityToolTip =>
            AdGuardDataAvailability != AdGuardAvailabilityState.Available
                ? "DNS activity is unavailable; router connection details remain available."
                : QueryLogAvailable
                ? "Values observed by AdGuard Home; DNS that bypasses AdGuard (including external encrypted DNS) is not observable."
                : "AdGuard query logging is disabled; router connection details remain available.";

        // Presentation metadata populated by ClientsViewModel.
        public string DeviceIcon { get; set; } = "●";
        public string DeviceType { get; set; } = "Unknown device";
        public string Manufacturer { get; set; } = "Unknown manufacturer";
        public string HealthText { get; set; } = "Unknown";
        public string HealthColour { get; set; } = "#687386";
        public bool IsFavorite { get; set; }
        public bool MonitorAvailability { get; set; }
        public bool NeedsReview { get; set; }

        // Live connection metadata from the GL.iNet client inventory.
        public string ConnectionType { get; set; } = "Unknown";
        public string WifiNetwork { get; set; } = "-";
        public string SignalStrength { get; set; } = "-";
        public string LiveInterface { get; set; } = "-";

        public bool IsEthernetConnection =>
            string.Equals(ConnectionType, "Ethernet", StringComparison.OrdinalIgnoreCase);

        public bool IsWifiConnection =>
            !IsEthernetConnection &&
            (ConnectionType.Contains("GHz", StringComparison.OrdinalIgnoreCase) ||
             WifiClientInfo.Useful(WifiNetwork));

        public string ConnectionSummary
        {
            get
            {
                if (IsEthernetConnection) return "Ethernet";
                if (!IsWifiConnection) return string.Empty;

                List<string> parts = new() { "Wi-Fi" };
                if (WifiClientInfo.Useful(ConnectionType)) parts.Add(ConnectionType);
                if (WifiClientInfo.Useful(WifiNetwork)) parts.Add(WifiNetwork);
                return string.Join(" • ", parts);
            }
        }

        // Reuse the established Wi-Fi signal categorisation rather than
        // introducing a second set of dBm thresholds for the Clients view.
        public string SignalQuality => new WifiClientInfo { Signal = SignalStrength }.SignalQuality;

        public bool HasConnectionSummary => !string.IsNullOrWhiteSpace(ConnectionSummary);

        public bool HasSignalSummary =>
            IsWifiConnection && SignalQuality != "—";

        public string SignalSummary =>
            HasSignalSummary ? $"{SignalQuality} • {SignalStrength}" : string.Empty;

        public string FirstSeenDisplay =>
            FirstSeenUtc == default ? "—" : FirstSeenUtc.ToLocalTime().ToString("g");

        public string LastObservedDisplay =>
            LastObservedUtc == default ? "—" : LastObservedUtc.ToLocalTime().ToString("g");

        public string FavoriteGlyph =>
            IsFavorite ? "★" : "☆";

        public string FavoriteToolTip =>
            IsFavorite
                ? "Remove from favourites"
                : "Add to favourites";
    }
}
