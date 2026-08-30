using System.Globalization;
using System.Text.RegularExpressions;
using RouterPilot.Models;

namespace RouterPilot.Presentation;

/// <summary>RouterPilot guidance for the GL-MT6000 (Flint 2) temperature card.</summary>
public static class RouterTemperatureHealth
{
    private static readonly Regex TemperaturePattern =
        new(@"[-+]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.Compiled);

    public static TemperatureHealthState Evaluate(string? model, string? displayValue)
    {
        if (!IsFlint2(model) || !TryParseCelsius(displayValue, out double celsius))
        {
            return TemperatureHealthState.Unavailable;
        }

        return celsius switch
        {
            < 65d => TemperatureHealthState.Normal,
            < 80d => TemperatureHealthState.Elevated,
            _ => TemperatureHealthState.High
        };
    }

    public static string Text(string? model, string? displayValue) =>
        Evaluate(model, displayValue) switch
        {
            TemperatureHealthState.Normal => "Normal",
            TemperatureHealthState.Elevated => "Elevated",
            TemperatureHealthState.High => "High",
            _ => RouterPilotStatusPresentation.NotAvailable
        };

    public static string Colour(string? model, string? displayValue) =>
        Evaluate(model, displayValue) switch
        {
            TemperatureHealthState.Normal => RouterPilotStatusPresentation.Colour(RouterPilotStatus.Active),
            TemperatureHealthState.Elevated => RouterPilotStatusPresentation.Colour(RouterPilotStatus.Pending),
            TemperatureHealthState.High => RouterPilotStatusPresentation.Colour(RouterPilotStatus.Error),
            _ => RouterPilotStatusPresentation.Colour(RouterPilotStatus.NotAvailable)
        };

    public static string ToolTip(string? model, string? displayValue) =>
        Evaluate(model, displayValue) switch
        {
            TemperatureHealthState.Normal => "Temperature is within the normal RouterPilot range for this router.",
            TemperatureHealthState.Elevated => "Router temperature is elevated.",
            TemperatureHealthState.High => "Router temperature is high. Check ventilation and ambient temperature.",
            _ => "Router temperature is unavailable."
        };

    public static bool IsFlint2(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        (model.Contains("GL-MT6000", StringComparison.OrdinalIgnoreCase) ||
         model.Contains("Flint 2", StringComparison.OrdinalIgnoreCase));

    private static bool TryParseCelsius(string? value, out double celsius)
    {
        celsius = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-") return false;
        Match match = TemperaturePattern.Match(value);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out celsius) && double.IsFinite(celsius);
    }
}

public enum TemperatureHealthState
{
    Unavailable,
    Normal,
    Elevated,
    High
}
