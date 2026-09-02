namespace RouterPilot.Models
{
    public class WifiClientInfo
    {
        public string Name { get; set; } = "Unknown device";
        public string IpAddress { get; set; } = "-";
        public string MacAddress { get; set; } = "-";
        public string Signal { get; set; } = "-";
        public string Band { get; set; } = "-";
        public string Interface { get; set; } = "-";
        public string Ssid { get; set; } = "-";
        public string Radio { get; set; } = "-";
        public string Channel { get; set; } = "-";
        public string ChannelWidth { get; set; } = "N/A";

        public string SignalQuality
        {
            get
            {
                string numeric = Signal.Replace("dBm", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                if (!int.TryParse(numeric, out int dbm)) return "—";
                return dbm >= -50 ? "Excellent" : dbm >= -60 ? "Good" : dbm >= -70 ? "Fair" : "Poor";
            }
        }

        public string DisplayIpAddress => Useful(IpAddress) ? IpAddress : "—";
        public string DisplayMacAddress => Useful(MacAddress) ? MacAddress : "—";
        public string DisplaySignal => Useful(Signal) ? Signal : "—";

        public static bool Useful(string? value) => !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("-", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
            !value.Equals("Not reported", StringComparison.OrdinalIgnoreCase);
    }
}
