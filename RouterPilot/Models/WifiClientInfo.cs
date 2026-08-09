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

        public string SignalQuality
        {
            get
            {
                string numeric = Signal.Replace("dBm", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                if (!int.TryParse(numeric, out int dbm)) return "N/A";
                return dbm >= -50 ? "Excellent" : dbm >= -60 ? "Good" : dbm >= -70 ? "Fair" : "Poor";
            }
        }
    }
}
