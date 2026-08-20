using System;

namespace RouterPilot.Services
{
    public static class ClientRefreshNotifier
    {
        public static event EventHandler? RefreshRequested;
        public static event EventHandler? ProfileStateChanged;

        public static void RequestRefresh()
        {
            RefreshRequested?.Invoke(
                null,
                EventArgs.Empty);
        }

        public static void NotifyProfileStateChanged()
        {
            ProfileStateChanged?.Invoke(
                null,
                EventArgs.Empty);
        }
    }
}
