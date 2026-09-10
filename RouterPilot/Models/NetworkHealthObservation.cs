namespace RouterPilot.Models;

public sealed record NetworkHealthObservation(
    string Domain,
    string Title,
    string State,
    string Summary,
    string Evidence,
    string CanonicalDestination)
{
    public bool HasNavigationTarget => RouterPilot.Services.NetworkHealthNavigationTarget.IsSupported(CanonicalDestination);
}
