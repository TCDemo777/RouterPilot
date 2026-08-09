using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static class PortForwardEditorDialog
{
    public static void Show(Window? owner, string title, PortForwardRuleInfo? existing, IEnumerable<DhcpLeaseInfo> leases, Func<PortForwardRuleRequest, Task<string?>> saveAsync)
    {
        var name = Input(new TextBox { Text = existing?.Name ?? string.Empty, MinWidth = 300 });
        var protocol = Input(new ComboBox { MinWidth = 180, SelectedValuePath = "Tag" });
        protocol.Items.Add(new ComboBoxItem { Content = "TCP", Tag = "tcp" });
        protocol.Items.Add(new ComboBoxItem { Content = "UDP", Tag = "udp" });
        protocol.Items.Add(new ComboBoxItem { Content = "TCP + UDP", Tag = "tcp udp" });
        protocol.SelectedValue = existing?.Protocol ?? "tcp";
        var externalPort = Input(new TextBox { Text = existing?.ExternalPort ?? string.Empty, MinWidth = 180 });
        var choices = leases.Where(item => !string.IsNullOrWhiteSpace(item.IpAddress) && item.IpAddress != "-")
            .Select(item => new DeviceChoice(string.IsNullOrWhiteSpace(item.ClientName) ? item.Hostname : item.ClientName, item.IpAddress))
            .DistinctBy(item => item.IpAddress).OrderBy(item => item.Display).ToList();
        var device = Input(new ComboBox { IsEditable = true, MinWidth = 300, ItemsSource = choices, DisplayMemberPath = nameof(DeviceChoice.Display) });
        DeviceChoice? existingChoice = choices.FirstOrDefault(item => item.IpAddress == existing?.DestinationIp);
        if (existingChoice is not null) device.SelectedItem = existingChoice;
        else device.Text = existing?.DestinationIp ?? string.Empty;
        var internalPort = Input(new TextBox { Text = existing?.InternalPort ?? string.Empty, MinWidth = 180 });
        var enabled = new CheckBox { Content = "Enabled", IsChecked = existing?.Enabled ?? true };
        var error = new TextBlock { Foreground = owner?.TryFindResource("Brush.Warning") as Brush ?? Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        var form = new Grid { Margin = new Thickness(20) };
        for (int row = 0; row < 8; row++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, 0, "Name", name); AddRow(form, 1, "Protocol", protocol); AddRow(form, 2, "External Port", externalPort); AddRow(form, 3, "Internal Device / IP", device); AddRow(form, 4, "Internal Port", internalPort); AddRow(form, 5, string.Empty, enabled);
        var note = new TextBlock { Text = "Standard WAN to LAN forwarding is used. Ports must be single values from 1 to 65535.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); Grid.SetRow(note, 6); Grid.SetColumn(note, 1); form.Children.Add(note);
        Grid.SetRow(error, 7); Grid.SetColumn(error, 1); form.Children.Add(error);

        var save = Button(owner, "Save", "Button.Primary"); var cancel = Button(owner, "Cancel", "Button.Secondary"); cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) }; buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        var dialog = new Window { Title = title, Content = panel, Width = 540, SizeToContent = SizeToContent.Height, MinWidth = 470, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        dialog.SetResourceReference(Window.ForegroundProperty, "Brush.Primary");
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            error.Text = string.Empty; save.IsEnabled = false; cancel.IsEnabled = false;
            string selectedProtocol = protocol.SelectedValue as string ?? "";
            string destinationIp = device.SelectedItem is DeviceChoice choice ? choice.IpAddress : device.Text.Trim();
            string? failure = await saveAsync(new PortForwardRuleRequest { Name = name.Text.Trim(), Protocol = selectedProtocol, SourceZone = "wan", ExternalPort = externalPort.Text.Trim(), DestinationZone = "lan", DestinationIp = destinationIp, InternalPort = internalPort.Text.Trim(), Enabled = enabled.IsChecked == true });
            if (failure is null) { dialog.DialogResult = true; dialog.Close(); return; }
            error.Text = failure; save.IsEnabled = true; cancel.IsEnabled = true;
        };
        dialog.ShowDialog();
    }

    private static void AddRow(Grid grid, int row, string label, Control input)
    {
        if (!string.IsNullOrEmpty(label)) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) }; text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); Grid.SetRow(text, row); Grid.SetColumn(text, 0); grid.Children.Add(text); }
        input.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }
    private static Button Button(Window? owner, string content, string style) { var result = new Button { Content = content }; if (owner?.TryFindResource(style) is Style resource) result.Style = resource; return result; }
    private static T Input<T>(T control) where T : Control
    {
        control.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceMuted");
        control.SetResourceReference(Control.ForegroundProperty, "Brush.Primary");
        control.SetResourceReference(Control.BorderBrushProperty, "Brush.BorderStrong");
        return control;
    }
    private sealed record DeviceChoice(string Name, string IpAddress)
    {
        public string Display => $"{Name} — {IpAddress}";
    }
}
