namespace RouterPilot.Models
{
    public sealed class DnsResolverPathPresentation
    {
        private const string UnavailableDisplay = "Unavailable";

        public string State { get; init; } = "Unavailable";
        public string Explanation { get; init; } = "Resolver information is unavailable.";
        public string RouterAddressDisplay { get; init; } = UnavailableDisplay;
        public string UpstreamDisplay { get; init; } = UnavailableDisplay;

        public static DnsResolverPathPresentation Create(
            string? upstreamDns,
            string? routerLanAddress,
            bool wanConnected)
        {
            var upstream = ParseResolvers(upstreamDns);
            var routerLan = ParseResolvers(routerLanAddress);
            string routerLanDisplay = FormatResolvers(routerLan);
            string upstreamDisplay = wanConnected ? FormatResolvers(upstream) : UnavailableDisplay;

            if (!wanConnected)
            {
                return new DnsResolverPathPresentation
                {
                    State = "Unavailable",
                    Explanation = "WAN is disconnected, so current upstream DNS is unavailable.",
                    RouterAddressDisplay = routerLanDisplay,
                    UpstreamDisplay = upstreamDisplay
                };
            }

            if (upstream.Count == 0 && routerLan.Count == 0)
            {
                return new DnsResolverPathPresentation
                {
                    State = "Unavailable",
                    Explanation = "Router LAN and upstream resolver information is unavailable.",
                    RouterAddressDisplay = routerLanDisplay,
                    UpstreamDisplay = upstreamDisplay
                };
            }

            if (upstream.Count == 0 || routerLan.Count == 0)
            {
                return new DnsResolverPathPresentation
                {
                    State = "Partial data",
                    Explanation = "Router LAN or upstream resolver information is incomplete.",
                    RouterAddressDisplay = routerLanDisplay,
                    UpstreamDisplay = upstreamDisplay
                };
            }

            return new DnsResolverPathPresentation
            {
                State = "Current router context",
                Explanation = "Client DNS configuration is not currently available.",
                RouterAddressDisplay = routerLanDisplay,
                UpstreamDisplay = upstreamDisplay
            };
        }

        private static List<string> ParseResolvers(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return [];

            var resolvers = new List<string>();
            foreach (string token in value.Split([',', ';', '\r', '\n', ' '], System.StringSplitOptions.RemoveEmptyEntries))
            {
                string resolver = token.Trim();
                if (IsUnavailable(resolver) || resolvers.Any(existing => SameAddress(existing, resolver))) continue;
                resolvers.Add(resolver);
            }

            return resolvers;
        }

        private static string FormatResolvers(IReadOnlyCollection<string> resolvers) =>
            resolvers.Count == 0 ? UnavailableDisplay : string.Join(" / ", resolvers);

        private static bool SameAddress(string first, string second)
        {
            if (System.Net.IPAddress.TryParse(first, out var firstAddress) && System.Net.IPAddress.TryParse(second, out var secondAddress))
                return firstAddress.Equals(secondAddress);

            return string.Equals(first, second, System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnavailable(string value) => value is "-" or "N/A" or "None" or "Unavailable" or "Unknown";
    }

    public class NetworkInfo
    {
        public bool Connected { get; set; }

        public string WanIp { get; set; } = "-";

        public string Gateway { get; set; } = "-";

        public string ExternalDns { get; set; } = "-";

        public string RouterLanAddress { get; set; } = "-";

        // Compatibility alias: this source is the router LAN address, not an
        // independently observed DHCP-advertised DNS setting.
        public string AdvertisedDns
        {
            get => RouterLanAddress;
            set => RouterLanAddress = value;
        }

        public string Latency { get; set; } = "-";


        //
        // Backwards compatibility
        // Existing Dashboard code uses this
        //

        public string DnsServer
        {
            get
            {
                return ExternalDns;
            }

            set
            {
                ExternalDns = value;
            }
        }
    }
}
