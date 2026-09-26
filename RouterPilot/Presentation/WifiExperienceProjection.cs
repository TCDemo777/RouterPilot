using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>Pure presentation of accepted router Wi-Fi data and the shared current-device inventory.</summary>
public static class WifiExperienceProjection
{
    public const int DevicePreviewLimit = 4;

    public static WifiExperienceSnapshot Create(
        IEnumerable<WifiRadioInfo>? radios,
        string? refreshError,
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion)
    {
        List<WifiRadioInfo> acceptedRadios = radios?.ToList() ?? [];
        int activeCount = acceptedRadios.Count(radio => radio.StatusDisplay == RouterPilotStatusPresentation.Active);
        int disabledCount = acceptedRadios.Count(radio => radio.StatusDisplay == RouterPilotStatusPresentation.Disabled);
        int unknownCount = acceptedRadios.Count - activeCount - disabledCount;
        string[] bands = acceptedRadios
            .Select(radio => Useful(radio.Band) ? radio.Band.Trim() : null)
            .Where(band => band is not null)
            .Select(band => band!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(BandOrder)
            .ThenBy(band => band, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string radioSummary = acceptedRadios.Count == 0
            ? string.IsNullOrWhiteSpace(refreshError)
                ? "Waiting for accepted router Wi-Fi information."
                : "Wireless network information is unavailable."
            : string.Join(" · ", new[]
            {
                activeCount > 0 ? $"{activeCount} active" : null,
                disabledCount > 0 ? $"{disabledCount} disabled" : null,
                unknownCount > 0 ? $"{unknownCount} status not reported" : null
            }.Where(value => value is not null));

        string radioDetail = !string.IsNullOrWhiteSpace(refreshError)
            ? refreshError
            : acceptedRadios.Count > 0
                ? "Showing the latest accepted router radio information."
                : "RouterPilot has not received a usable wireless-interface snapshot yet.";

        List<(DeviceObservation Observation, string Name)> wifiDevices = [];
        bool inventoryAccepted = IsAcceptedForCurrentContext(
            inventory,
            expectedRouterProfileId,
            expectedContextVersion);

        if (inventoryAccepted)
        {
            wifiDevices = inventory!.Observations
                .Where(pair => IsValidObservation(pair.Key, pair.Value) &&
                    pair.Value.IsOnline != false &&
                    pair.Value.Client.IsWifiConnection)
                .Select(pair => (Observation: pair.Value, Name: DisplayName(pair.Value.Client)))
                .OrderByDescending(item => item.Observation.IsOnline == true)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Observation.Identity.CanonicalMac, StringComparer.Ordinal)
                .ToList();
        }

        WifiCurrentDeviceItem[] preview = wifiDevices
            .Take(DevicePreviewLimit)
            .Select(item => new WifiCurrentDeviceItem(
                item.Name,
                item.Observation.Client.ConnectionSummary,
                item.Observation.IsOnline == true
                    ? "Online"
                    : "Observed · online status not confirmed",
                item.Observation.IsOnline == true))
            .ToArray();

        string deviceSummary = !inventoryAccepted
            ? "Waiting for current Wi-Fi device information…"
            : wifiDevices.Count == 0
                ? "No current Wi-Fi device observations are available."
                : $"{wifiDevices.Count} Wi-Fi {(wifiDevices.Count == 1 ? "device" : "devices")} currently observed";

        return new WifiExperienceSnapshot(
            acceptedRadios.Count,
            acceptedRadios.Count == 0
                ? string.IsNullOrWhiteSpace(refreshError) ? "Waiting" : "Unavailable"
                : acceptedRadios.Count == 1 ? "1 reported network" : $"{acceptedRadios.Count} reported networks",
            radioSummary,
            bands.Length == 0 ? "Bands not reported" : string.Join(" · ", bands),
            radioDetail,
            inventoryAccepted,
            deviceSummary,
            wifiDevices.Count,
            preview);
    }

    private static bool IsAcceptedForCurrentContext(
        DeviceInventorySnapshot? inventory,
        string? expectedRouterProfileId,
        long expectedContextVersion) =>
        inventory is not null &&
        !string.IsNullOrWhiteSpace(inventory.RouterProfileId) &&
        inventory.ContextVersion > 0 &&
        !string.IsNullOrWhiteSpace(expectedRouterProfileId) &&
        expectedContextVersion > 0 &&
        inventory.ContextVersion == expectedContextVersion &&
        string.Equals(inventory.RouterProfileId, expectedRouterProfileId, StringComparison.Ordinal);

    private static bool IsValidObservation(DeviceIdentity identity, DeviceObservation observation) =>
        DeviceIdentity.TryCreate(identity.CanonicalMac, out DeviceIdentity keyIdentity) &&
        keyIdentity == observation.Identity &&
        observation.Client is not null;

    private static string DisplayName(ClientInfo client) =>
        Useful(client.Name) ? client.Name : "Unknown device";

    private static bool Useful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "-" and not "—" and not "N/A";

    private static int BandOrder(string band) => band.StartsWith("2.4", StringComparison.OrdinalIgnoreCase)
        ? 0
        : band.StartsWith("5", StringComparison.OrdinalIgnoreCase)
            ? 1
            : band.StartsWith("6", StringComparison.OrdinalIgnoreCase)
                ? 2
                : 3;
}

public sealed record WifiExperienceSnapshot(
    int ReportedNetworkCount,
    string ReportedNetworkCountDisplay,
    string RadioSummary,
    string ReportedBands,
    string RadioDetail,
    bool HasAcceptedDeviceInventory,
    string WifiDeviceSummary,
    int WifiDeviceCount,
    IReadOnlyList<WifiCurrentDeviceItem> WifiDevices);

public sealed record WifiCurrentDeviceItem(
    string Name,
    string Connection,
    string PresenceDisplay,
    bool IsExplicitlyOnline);
