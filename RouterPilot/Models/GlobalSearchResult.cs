namespace RouterPilot.Models
{
    public class GlobalSearchResult
    {
        public string Category { get; set; } = "-";
        public string Title { get; set; } = "-";
        public string Subtitle { get; set; } = "-";
        public string Detail { get; set; } = "-";
        public string BadgeText { get; set; } = "-";
        public string BadgeColour { get; set; } = "#687386";
        public string NavigationTarget { get; init; } = string.Empty;
        public string EntityId { get; init; } = string.Empty;
        public string SearchTerms { get; init; } = string.Empty;
        public int SortPriority { get; init; }
    }
}
