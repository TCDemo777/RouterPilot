using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.Views;
public partial class RouterProfilesWindow : Window
{
    private readonly IRouterProfileService _profiles = ((App)Application.Current).Services.GetRequiredService<IRouterProfileService>();
    private readonly IRouterSwitchCoordinator _switcher = ((App)Application.Current).Services.GetRequiredService<IRouterSwitchCoordinator>();
    public ObservableCollection<Row> Items { get; } = [];
    public RouterProfilesWindow() { InitializeComponent(); Profiles.ItemsSource = Items; _switcher.Switched += Switcher_Switched; Reload(); }
    private void Switcher_Switched(object? sender, RouterProfile profile)
    {
        if (Dispatcher.CheckAccess()) Reload(); else _ = Dispatcher.InvokeAsync(Reload);
    }
    protected override void OnClosed(EventArgs e) { _switcher.Switched -= Switcher_Switched; base.OnClosed(e); }
    private void Reload() { Items.Clear(); string active = _profiles.GetActiveProfile()?.Id ?? ""; int count = _profiles.GetProfiles().Count; foreach (var p in _profiles.GetProfiles()) Items.Add(new Row(p, p.Id == active, count > 1)); }
    private async void Switch_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement { Tag: Row row }) { await _switcher.SwitchAsync(row.Profile.Id); Reload(); } }
    private void Add_Click(object s, RoutedEventArgs e) { var dialog = new RouterProfileEditorWindow(); if (dialog.ShowDialog() == true) _profiles.SaveProfile(dialog.Profile!); Reload(); }
    private void Edit_Click(object s, RoutedEventArgs e) { if (s is FrameworkElement { Tag: Row row }) { var dialog = new RouterProfileEditorWindow(row.Profile); if (dialog.ShowDialog() == true) _profiles.SaveProfile(dialog.Profile!); Reload(); } }
    private void Remove_Click(object s, RoutedEventArgs e) { if (s is not FrameworkElement { Tag: Row row }) return; if (MessageBox.Show($"Remove router?\n\n{row.Profile.DisplayName} will be removed from RouterPilot.", "Remove router", MessageBoxButton.OKCancel) == MessageBoxResult.OK && !_profiles.RemoveInactiveProfile(row.Profile.Id)) MessageBox.Show("Switch to another router before removing this profile.", "RouterPilot"); Reload(); }
    private void Close_Click(object s, RoutedEventArgs e) => Close();
    public sealed record Row(RouterProfile Profile, bool IsActive, bool Multiple) { public string DisplayName => Profile.DisplayName; public string RouterHost => Profile.RouterHost; public string StatusLabel => IsActive ? "Active" : "Inactive"; public bool CanSwitch => !IsActive; public bool CanRemove => Multiple && !IsActive; }
}
