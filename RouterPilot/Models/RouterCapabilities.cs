namespace RouterPilot.Models
{
    /// <summary>
    /// Capability state observed for the active router session. A capability is
    /// enabled only after its safe read path has returned usable data; it is not
    /// inferred from a router model or firmware family.
    /// </summary>
    public sealed class RouterCapabilities
    {
        public WifiRouterCapabilities WiFi { get; } = new();
        public DhcpRouterCapabilities Dhcp { get; } = new();
    }

    public sealed class WifiRouterCapabilities
    {
        public bool Read { get; internal set; }
        public bool GuestRead { get; internal set; }
        public bool ClientRead { get; internal set; }
        public bool SignalRead { get; internal set; }
        public bool ChannelWidthRead { get; internal set; }
        public bool TransmitPowerRead { get; internal set; }

        // Sprint 1 is deliberately observational. These stay false until a
        // typed, verified and recoverable write contract is implemented.
        public bool RadioControl { get; internal set; }
        public bool GuestControl { get; internal set; }
        public bool SecurityWrite { get; internal set; }
        public bool ChannelWrite { get; internal set; }
    }

    public sealed class DhcpRouterCapabilities
    {
        public bool Read { get; internal set; }
        public bool ActiveLeases { get; internal set; }
        public bool ReservationsRead { get; internal set; }

        // Read-only Sprint 2: no mutation contract has been verified.
        public bool ReservationsWrite { get; internal set; }
        public bool RangeWrite { get; internal set; }
        public bool LeaseTimeWrite { get; internal set; }
    }
}
