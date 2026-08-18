using System.Collections.ObjectModel;
using System.Linq;

namespace RouterPilot.Models
{
    public enum WifiGuestClassification
    {
        Unknown,
        LikelyGuest,
        VerifiedGuest
    }

    public class WifiRadioInfo
    {
        public string Radio { get; set; } = "-";
        public string Interface { get; set; } = "-";
        public string Ssid { get; set; } = "-";
        public string Band { get; set; } = "-";
        public string HardwareMode { get; set; } = "N/A";
        public string Channel { get; set; } = "-";
        public string ChannelWidth { get; set; } = "N/A";
        // No effective transmit-power read is performed in Sprint 1. The field
        // is retained for a future verified read source without guessing.
        public string TransmitPower { get; set; } = "N/A";
        public string Security { get; set; } = "-";
        public string NetworkAssociation { get; set; } = "N/A";
        public WifiGuestClassification GuestClassification { get; set; }
        public string GuestClassificationDisplay => GuestClassification switch
        {
            WifiGuestClassification.VerifiedGuest => "Guest network",
            WifiGuestClassification.LikelyGuest => "Likely guest network",
            _ => ""
        };
        public bool IsGuestNetwork => GuestClassification != WifiGuestClassification.Unknown;
        public bool IsVerifiedGuestNetwork => GuestClassification == WifiGuestClassification.VerifiedGuest;
        public string Source { get; set; } = "UCI / runtime status";
        public string Status { get; set; } = RouterPilotStatusPresentation.NotAvailable;

        public string StatusDisplay => Status.Trim().ToLowerInvariant() switch
        {
            "disabled" or "down" => RouterPilotStatusPresentation.Disabled,
            "active" or "configured" or "online" or "running" or "up" =>
                RouterPilotStatusPresentation.Active,
            _ => RouterPilotStatusPresentation.NotAvailable
        };

        public string StatusColour => RouterPilotStatusPresentation.Colour(Status.Trim().ToLowerInvariant() switch
        {
            "disabled" or "down" => RouterPilotStatus.Disabled,
            "active" or "configured" or "online" or "running" or "up" =>
                RouterPilotStatus.Active,
            _ => RouterPilotStatus.NotAvailable
        });
        public int ClientCount => Clients.Count;
        public bool HasUsefulClientIpAddress => Clients.Any(client => WifiClientInfo.Useful(client.IpAddress));
        public bool HasUsefulClientMacAddress => Clients.Any(client => WifiClientInfo.Useful(client.MacAddress));
        public bool HasUsefulClientSignal => Clients.Any(client => WifiClientInfo.Useful(client.Signal));
        public string ClientCountDisplay => ClientCount == 1 ? "1 client" : $"{ClientCount} clients";
        public bool IsExpanded { get; set; }
        public string ChannelDisplay => Channel.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? "Channel Auto"
            : WifiClientInfo.Useful(Channel)
                ? $"Channel {Channel}"
                : string.Empty;
        public string ChannelSummary => string.Join(" • ", new[] { ChannelDisplay, ChannelWidth }
            .Where(WifiClientInfo.Useful));
        public string CollapsedSummary => string.Join(" • ", new[]
            { Band, ChannelSummary, Security, ClientCountDisplay }.Where(WifiClientInfo.Useful));
        public bool HasAverageSignal => TryGetAverageSignal(out _);
        public string AverageSignalDisplay => TryGetAverageSignal(out int average)
            ? $"{average} dBm • {new WifiClientInfo { Signal = average.ToString() }.SignalQuality}"
            : string.Empty;
        public ObservableCollection<WifiClientInfo> Clients { get; } = new();

        private bool TryGetAverageSignal(out int average)
        {
            List<int> samples = Clients
                .Select(client => client.Signal.Replace("dBm", string.Empty, StringComparison.OrdinalIgnoreCase).Trim())
                .Select(value => int.TryParse(value, out int signal) ? (int?)signal : null)
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .ToList();

            average = samples.Count == 0 ? 0 : (int)Math.Round(samples.Average());
            return samples.Count > 0;
        }
    }
}
