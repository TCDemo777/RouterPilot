using System;

namespace RouterPilot.Models
{
    public class AdGuardProtectionStatus
    {
        public bool IsEnabled { get; set; }

        public bool IsPaused { get; set; }

        public TimeSpan RemainingPause { get; set; }

        // A timed pause is a confirmed disabled state with an authoritative
        // resume duration, not an operation still pending on AdGuard Home.
        public string StateText => IsEnabled
            ? RouterPilotStatusPresentation.Active
            : RouterPilotStatusPresentation.Disabled;
    }
}
