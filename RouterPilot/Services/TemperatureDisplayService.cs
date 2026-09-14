using RouterPilot.Models;

namespace RouterPilot.Services;

/// <summary>
/// Formats router temperatures from their authoritative Celsius telemetry value.
/// Changing the unit only affects presentation and never requests router data.
/// </summary>
public sealed class TemperatureDisplayService
{
    private TemperatureUnit _unit = TemperatureUnit.Celsius;

    public event EventHandler? UnitChanged;

    public TemperatureUnit Unit => _unit;

    public void SetUnit(TemperatureUnit unit)
    {
        TemperatureUnit normalized = Enum.IsDefined(unit)
            ? unit
            : TemperatureUnit.Celsius;

        if (_unit == normalized)
            return;

        _unit = normalized;
        UnitChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Format(double? celsius) =>
        celsius is double value && double.IsFinite(value)
            ? Format(value)
            : "—";

    public string Format(double celsius)
    {
        double displayValue = _unit == TemperatureUnit.Fahrenheit
            ? celsius * 9d / 5d + 32d
            : celsius;

        return $"{displayValue:0.#} {(_unit == TemperatureUnit.Fahrenheit ? "°F" : "°C")}";
    }
}
