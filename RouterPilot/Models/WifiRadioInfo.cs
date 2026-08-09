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
        public ObservableCollection<WifiClientInfo> Clients { get; } = new();
    }
}
