namespace RouterPilot.Models
{
    public class QueryLogEntry
    {
        public string Time { get; set; } = "";
        public DateTimeOffset? Timestamp { get; set; }
        public string Client { get; set; } = "";
        public string ClientAddress { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string Domain { get; set; } = "";
        public bool IsBlocked { get; set; }

        public string Status =>
            IsBlocked ? "Blocked" : "Allowed";
    }
}
