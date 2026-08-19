using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static partial class PortForwardEditorDialog
{
    private const double CompactInputHeight = 30;

    public static void Show(Window? owner, string title, PortForwardRuleInfo? existing, IEnumerable<DhcpLeaseInfo> leases, Func<PortForwardRuleRequest, Task<string?>> saveAsync)
    {
        var draft = new PortForwardDraft { InternalIp = existing?.DestinationIp ?? string.Empty };
        var name = Input(new TextBox { Text = existing?.Name ?? string.Empty, MinWidth = 300 });
        var protocol = Input(new ComboBox { MinWidth = 180, SelectedValuePath = "Tag" });
        protocol.Items.Add(new ComboBoxItem { Content = "TCP", Tag = "tcp" });
        protocol.Items.Add(new ComboBoxItem { Content = "UDP", Tag = "udp" });
        protocol.Items.Add(new ComboBoxItem { Content = "TCP + UDP", Tag = "tcp udp" });
        protocol.SelectedValue = existing?.Protocol ?? "tcp";
        var externalPort = Input(new TextBox { Text = existing?.ExternalPort ?? string.Empty, MinWidth = 180 });
        var preset = Input(new ComboBox
        {
            MinWidth = 300,
            ItemsSource = PortForwardPresetCatalog.All,
            ItemTemplate = PresetTemplate()
        });
        // Materialise a dialog-local snapshot. Dashboard DHCP refreshes replace
        // their lease objects, but an in-progress port-forward draft must keep
        // its device identity and target address stable.
        var choices = leases.Where(item => !string.IsNullOrWhiteSpace(item.IpAddress) && item.IpAddress != "-")
            .Select(item => new DeviceChoice(
                DisplayName(item),
                item.IpAddress,
                DeviceIdentity(item.MacAddress, item.IpAddress)))
            .DistinctBy(item => item.Identity).OrderBy(item => item.Name).ThenBy(item => item.IpAddress).ToList();
        var device = Input(new ComboBox
        {
            IsEditable = false,
            MinWidth = 300,
            ItemsSource = choices,
            ItemTemplate = DeviceChoiceTemplate(),
            SelectedValuePath = nameof(DeviceChoice.Identity),
            DataContext = draft
        });
        device.SetBinding(Selector.SelectedValueProperty, new Binding(nameof(PortForwardDraft.SelectedInternalDeviceId))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        DeviceChoice? existingChoice = choices.FirstOrDefault(item => item.IpAddress == existing?.DestinationIp);
        if (existingChoice is not null) draft.SelectedInternalDeviceId = existingChoice.Identity;
        device.SelectionChanged += (_, _) =>
        {
            if (device.SelectedItem is DeviceChoice choice)
                draft.InternalIp = choice.IpAddress;
        };
        // Keep the collapsed picker concise. The dropdown retains the two-column
        // template, while this non-interactive overlay covers its selected IP.
        var deviceHost = new Grid();
        deviceHost.Children.Add(device);
        var selectedName = new TextBlock
        {
            IsHitTestVisible = false,
            Margin = new Thickness(10, 0, 32, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        selectedName.SetResourceReference(TextBlock.BackgroundProperty, "Brush.SurfaceMuted");
        selectedName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        selectedName.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Name") { Source = device });
        deviceHost.Children.Add(selectedName);
        var internalIp = Input(new TextBox { MinWidth = 300, DataContext = draft });
        internalIp.SetBinding(TextBox.TextProperty, new Binding(nameof(PortForwardDraft.InternalIp))
        {
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
        var internalPort = Input(new TextBox { Text = existing?.InternalPort ?? string.Empty, MinWidth = 180 });
        var enabled = new CheckBox { Content = "Enabled", IsChecked = existing?.Enabled ?? true };
        var error = new TextBlock { Foreground = owner?.TryFindResource("Brush.Warning") as Brush ?? Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        bool applyingPreset = false;
        void UpdatePresetFromFields()
        {
            if (applyingPreset) return;
            string currentProtocol = protocol.SelectedValue as string ?? string.Empty;
            PortForwardPreset match = PortForwardPresetCatalog.Match(currentProtocol, externalPort.Text.Trim(), internalPort.Text.Trim());
            if (!ReferenceEquals(preset.SelectedItem, match)) preset.SelectedItem = match;
        }
        preset.SelectionChanged += (_, _) =>
        {
            if (preset.SelectedItem is not PortForwardPreset selected || selected.IsCustom || applyingPreset) return;
            applyingPreset = true;
            protocol.SelectedValue = selected.Protocol;
            externalPort.Text = selected.ExternalPort;
            internalPort.Text = selected.InternalPort;
            applyingPreset = false;
        };
        protocol.SelectionChanged += (_, _) => UpdatePresetFromFields();
        externalPort.TextChanged += (_, _) => UpdatePresetFromFields();
        internalPort.TextChanged += (_, _) => UpdatePresetFromFields();
        preset.SelectedItem = PortForwardPresetCatalog.Match(protocol.SelectedValue as string, externalPort.Text.Trim(), internalPort.Text.Trim());
        var presetHost = new Grid();
        presetHost.Children.Add(preset);
        var selectedPresetName = new TextBlock
        {
            IsHitTestVisible = false,
            Margin = new Thickness(10, 0, 32, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        selectedPresetName.SetResourceReference(TextBlock.BackgroundProperty, "Brush.SurfaceMuted");
        selectedPresetName.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        selectedPresetName.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Name") { Source = preset });
        presetHost.Children.Add(selectedPresetName);

        var form = new Grid { Margin = new Thickness(20) };
        for (int row = 0; row < 10; row++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, 0, "Name", name); AddRow(form, 1, "Service / Preset", presetHost); AddRow(form, 2, "Protocol", protocol); AddRow(form, 3, "External Port", externalPort); AddRow(form, 4, "Internal Device", deviceHost); AddRow(form, 5, "Internal IP", internalIp); AddRow(form, 6, "Internal Port", internalPort); AddRow(form, 7, string.Empty, enabled);
        var note = new TextBlock { Text = "Only forward ports for services you intend to expose to the Internet. Ports must be single values from 1 to 65535.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); Grid.SetRow(note, 8); Grid.SetColumn(note, 1); form.Children.Add(note);
        Grid.SetRow(error, 9); Grid.SetColumn(error, 1); form.Children.Add(error);

        var save = Button(owner, "Save", "Button.Primary"); var cancel = Button(owner, "Cancel", "Button.Secondary"); cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) }; buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        var dialog = new Window { Title = title, Content = panel, Width = 540, SizeToContent = SizeToContent.Height, MinWidth = 470, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        dialog.SetResourceReference(Window.ForegroundProperty, "Brush.TextPrimary");
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            error.Text = string.Empty; save.IsEnabled = false; cancel.IsEnabled = false;
            string selectedProtocol = protocol.SelectedValue as string ?? "";
            string? failure = await saveAsync(new PortForwardRuleRequest { Name = name.Text.Trim(), Protocol = selectedProtocol, SourceZone = "wan", ExternalPort = externalPort.Text.Trim(), DestinationZone = "lan", DestinationIp = draft.InternalIp.Trim(), InternalPort = internalPort.Text.Trim(), Enabled = enabled.IsChecked == true });
            if (failure is null) { dialog.DialogResult = true; dialog.Close(); return; }
            error.Text = failure; save.IsEnabled = true; cancel.IsEnabled = true;
        };
        dialog.ShowDialog();
    }

    private static void AddRow(Grid grid, int row, string label, UIElement input)
    {
        if (!string.IsNullOrEmpty(label)) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) }; text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); Grid.SetRow(text, row); Grid.SetColumn(text, 0); grid.Children.Add(text); }
        if (input is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }
    private static Button Button(Window? owner, string content, string style) { var result = new Button { Content = content }; if (owner?.TryFindResource(style) is Style resource) result.Style = resource; return result; }
    private static T Input<T>(T control) where T : Control
    {
        // Apply one compact baseline to every closed form input. Dropdown item
        // templates remain independent, so their pointer targets stay roomy.
        control.Height = CompactInputHeight;
        control.VerticalContentAlignment = VerticalAlignment.Center;
        switch (control)
        {
            case TextBox textBox:
                textBox.Padding = new Thickness(10, 4, 10, 4);
                break;
            case ComboBox comboBox:
                comboBox.Padding = new Thickness(10, 4, 10, 4);
                break;
        }
        control.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceMuted");
        control.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        control.SetResourceReference(Control.BorderBrushProperty, "Brush.BorderStrong");
        return control;
    }
    private static string DeviceIdentity(string? macAddress, string ipAddress)
    {
        string normalizedMac = new string((macAddress ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return normalizedMac.Length == 12 ? "mac:" + normalizedMac : "ip:" + ipAddress.Trim();
    }

    // ClientName already contains the RouterPilot profile/friendly-name overlay
    // applied by the existing DHCP snapshot path. The remaining fallbacks mirror
    // that path without bringing client diagnostics into this compact picker.
    private static string DisplayName(DhcpLeaseInfo lease)
    {
        if (!string.IsNullOrWhiteSpace(lease.ClientName) && !string.Equals(lease.ClientName, "Unknown device", StringComparison.OrdinalIgnoreCase))
            return lease.ClientName;
        if (!string.IsNullOrWhiteSpace(lease.Hostname) && !string.Equals(lease.Hostname, "Unknown device", StringComparison.OrdinalIgnoreCase))
            return lease.Hostname;
        return "Device";
    }

    private static DataTemplate DeviceChoiceTemplate() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid MinWidth="260" Margin="2,0">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
            <TextBlock Grid.Column="1" Margin="16,0,0,0" Text="{Binding IpAddress}" VerticalAlignment="Center" TextAlignment="Right"/>
          </Grid>
        </DataTemplate>
        """);

    private static DataTemplate PresetTemplate() => (DataTemplate)XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid MinWidth="260" Margin="2,0">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*"/>
              <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
            <TextBlock Grid.Column="1" Margin="16,0,0,0" Text="{Binding ExternalPort}" VerticalAlignment="Center" TextAlignment="Right"/>
          </Grid>
        </DataTemplate>
        """);

    private sealed record DeviceChoice(string Name, string IpAddress, string Identity)
    {
        public string Display => $"{Name} — {IpAddress}";
    }

    private sealed partial class PortForwardDraft : ObservableObject
    {
        [ObservableProperty] private string internalIp = string.Empty;
        [ObservableProperty] private string? selectedInternalDeviceId;
    }
}
