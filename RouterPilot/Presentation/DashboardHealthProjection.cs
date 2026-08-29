using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>
/// Deterministic dashboard health and internet-quality presentation policy.
/// This type deliberately has no router, UI, refresh, or persistence dependencies.
/// </summary>
public sealed class DashboardHealthProjection
{
    private DashboardHealthProjection(DashboardHealthInput input)
    {
        Score = CalculateScore(input);
        State = CalculateState(input, Score);
        Summary = State == RouterPilotStatusPresentation.Pending
            ? "Collecting router health…"
            : $"Health: {Score}% — {State}";
        Colour = RouterPilotStatusPresentation.Colour(State switch
        {
            "Excellent" or "Good" => RouterPilotStatus.Active,
            "Critical" => RouterPilotStatus.Error,
            "Attention Required" => RouterPilotStatus.Pending,
            _ => RouterPilotStatus.Pending
        });

        AttentionReasons = CalculateAttentionReasons(input, State);
        HealthyConditions = CalculateHealthyConditions(input, State);

        InternetQualityState = CalculateInternetQualityState(input);
        InternetQualityDetail = InternetQualityState is "Unavailable" or RouterPilotStatusPresentation.Pending or RouterPilotStatusPresentation.NotAvailable
            ? InternetQualityState
            : input.Latency;
        InternetQualityColour = RouterPilotStatusPresentation.Colour(InternetQualityState switch
        {
            "Excellent" or "Good" => RouterPilotStatus.Active,
            "Fair" => RouterPilotStatus.Pending,
            "Poor" => RouterPilotStatus.Error,
            _ => RouterPilotStatus.NotAvailable
        });
    }

    public int Score { get; }
    public string State { get; }
    public string Summary { get; }
    public string Colour { get; }
    public IReadOnlyList<string> AttentionReasons { get; }
    public IReadOnlyList<string> HealthyConditions { get; }
    public string InternetQualityState { get; }
    public string InternetQualityDetail { get; }
    public string InternetQualityColour { get; }

    public static DashboardHealthProjection Create(DashboardHealthInput input) => new(input);

    private static int CalculateScore(DashboardHealthInput input)
    {
        if (!input.RouterConnected)
            return 0;

        int score = 100;
        if (!input.InternetConnected) score -= 20;
        if (input.AdGuardExpected && !input.AdGuardAvailable) score -= 15;
        if (input.CpuPercentage >= 90) score -= 10;
        else if (input.CpuPercentage >= 70) score -= 5;
        if (input.MemoryPercentage >= 90) score -= 10;
        else if (input.MemoryPercentage >= 75) score -= 5;
        if (input.StoragePercentage >= 90) score -= 5;
        else if (input.StoragePercentage >= 75) score -= 3;
        if (input.FirmwareUpdateAvailable) score -= 3;
        return Math.Clamp(score, 0, 100);
    }

    private static string CalculateState(DashboardHealthInput input, int score) =>
        !input.RouterConnected ? "Critical" : input.CpuUtilisationPending ? RouterPilotStatusPresentation.Pending :
        score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 45 ? "Attention Required" : "Critical";

    private static IReadOnlyList<string> CalculateAttentionReasons(
        DashboardHealthInput input,
        string state)
    {
        if (state == RouterPilotStatusPresentation.Pending)
            return ["Waiting for router health data…"];

        List<string> reasons = [];
        if (!input.RouterConnected) reasons.Add("Router is disconnected");
        if (input.RouterConnected && !input.InternetConnected) reasons.Add("Internet connection is unavailable");
        if (input.RouterConnected && input.AdGuardExpected && !input.AdGuardAvailable) reasons.Add("AdGuard Home is unavailable");
        if (input.FirmwareUpdateAvailable) reasons.Add($"Firmware update available ({input.FirmwareLatestVersion})");
        if (input.CpuPercentage >= 90) reasons.Add("CPU usage is high");
        else if (input.CpuPercentage >= 70) reasons.Add("CPU usage is elevated");
        if (input.MemoryPercentage >= 90) reasons.Add("Memory usage is high");
        else if (input.MemoryPercentage >= 75) reasons.Add("Memory usage is elevated");
        if (input.StoragePercentage >= 90) reasons.Add("Storage is nearly full");
        else if (input.StoragePercentage >= 75) reasons.Add("Storage usage is elevated");
        return reasons.Take(3).ToList();
    }

    private static IReadOnlyList<string> CalculateHealthyConditions(
        DashboardHealthInput input,
        string state)
    {
        if (state == RouterPilotStatusPresentation.Pending || !input.RouterConnected)
            return [];

        List<string> conditions = [];
        if (input.InternetConnected) conditions.Add("Internet connected");
        if (input.AdGuardExpected && input.AdGuardAvailable) conditions.Add("AdGuard Home active");
        if (input.CpuPercentage is > 0 and < 70) conditions.Add("CPU normal");
        if (input.MemoryPercentage is > 0 and < 75) conditions.Add("Memory normal");
        if (input.FirmwareUpdateStatus == FirmwareUpdateCheckStatus.UpToDate) conditions.Add("Firmware up to date");
        return conditions.Take(3).ToList();
    }

    private static string CalculateInternetQualityState(DashboardHealthInput input)
    {
        if (!input.RouterConnected || !input.InternetConnected)
            return "Unavailable";

        if (!TryGetLatencyMilliseconds(input.Latency, out double milliseconds))
            return input.CpuUtilisationPending
                ? RouterPilotStatusPresentation.Pending
                : RouterPilotStatusPresentation.NotAvailable;

        return milliseconds <= 30 ? "Excellent" : milliseconds <= 80 ? "Good" : milliseconds <= 150 ? "Fair" : "Poor";
    }

    private static bool TryGetLatencyMilliseconds(string? latency, out double milliseconds)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            latency ?? string.Empty,
            @"(?<ms>\d+(?:[\.,]\d+)?)\s*ms",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return double.TryParse(
            match.Groups["ms"].Value.Replace(',', '.'),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out milliseconds);
    }
}

public sealed record DashboardHealthInput(
    bool RouterConnected,
    bool InternetConnected,
    bool AdGuardAvailable,
    bool AdGuardExpected,
    double CpuPercentage,
    bool CpuUtilisationPending,
    double MemoryPercentage,
    double StoragePercentage,
    bool FirmwareUpdateAvailable,
    FirmwareUpdateCheckStatus FirmwareUpdateStatus,
    string FirmwareLatestVersion,
    string Latency);
