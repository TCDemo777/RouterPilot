using System.Collections.Generic;

namespace RouterPilot.Models
{
    public class AdGuardStatistics
    {
        public int TotalQueries { get; set; }

        public int BlockedQueries { get; set; }

        /// <summary>
        /// The average DNS request processing time reported by AdGuard Home's
        /// statistics endpoint, in seconds.  A null value means the installed
        /// AdGuard Home did not provide a usable value.
        /// </summary>
        public double? AverageProcessingTimeSeconds { get; set; }

        public bool ProtectionEnabled { get; set; }

        public List<AdGuardTimePoint> QueryHistory { get; set; } =
            new();

        public string QueryHistoryTimeUnits { get; set; } = "hours";

        public List<AdGuardRankedItem> TopClients { get; set; } =
            new();

        public List<AdGuardRankedItem> TopQueriedDomains { get; set; } =
            new();

        public List<AdGuardRankedItem> TopBlockedDomains { get; set; } =
            new();

        public double BlockPercentage
        {
            get
            {
                if (TotalQueries <= 0)
                {
                    return 0;
                }

                return
                    (double)BlockedQueries /
                    TotalQueries *
                    100;
            }
        }
    }

}
