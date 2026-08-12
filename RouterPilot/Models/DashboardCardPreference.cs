using CommunityToolkit.Mvvm.ComponentModel;

namespace RouterPilot.Models;

public sealed partial class DashboardCardPreference : ObservableObject
{
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    private bool isVisible = true;

    [ObservableProperty]
    private int displayOrder;
}
