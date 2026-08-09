using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static class DhcpReservationEditorDialog
{
    public static void Show(Window? owner, string title, DhcpReservationRequest initial, Func<DhcpReservationRequest, Task<string?>> saveAsync)
    {
        var nameBox = new TextBox { Text = initial.Hostname ?? string.Empty, MinWidth = 310 };
        var macBox = new TextBox { Text = initial.MacAddress, MinWidth = 310 };
        var ipBox = new TextBox { Text = initial.IpAddress, MinWidth = 310 };
        var error = new TextBlock { Foreground = owner?.TryFindResource("Brush.Warning") as Brush ?? Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        var form = new Grid { Margin = new Thickness(20) };
        for (int row = 0; row < 5; row++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddFormRow(form, 0, "Device Name", nameBox);
        AddFormRow(form, 1, "MAC Address", macBox);
        AddFormRow(form, 2, "Reserved IP", ipBox);
        var note = new TextBlock { Text = "Device Name is display metadata and is not written to the router.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        Grid.SetRow(note, 3); Grid.SetColumn(note, 1); form.Children.Add(note);
        Grid.SetRow(error, 4); Grid.SetColumn(error, 1); form.Children.Add(error);

        var save = CreateButton(owner, "Save", "Button.Primary");
        var cancel = CreateButton(owner, "Cancel", "Button.Secondary");
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        var dialog = new Window { Title = title, Content = panel, Width = 510, SizeToContent = SizeToContent.Height, MinWidth = 440, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            error.Text = string.Empty;
            save.IsEnabled = false; cancel.IsEnabled = false;
            string? failure = await saveAsync(new DhcpReservationRequest { Hostname = nameBox.Text.Trim(), MacAddress = macBox.Text.Trim(), IpAddress = ipBox.Text.Trim() });
            if (failure is null) { dialog.DialogResult = true; dialog.Close(); return; }
            error.Text = failure;
            save.IsEnabled = true; cancel.IsEnabled = true;
        };
        dialog.ShowDialog();
    }

    private static void AddFormRow(Grid form, int row, string label, Control input)
    {
        var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        input.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(labelBlock, row); Grid.SetColumn(labelBlock, 0);
        Grid.SetRow(input, row); Grid.SetColumn(input, 1);
        form.Children.Add(labelBlock); form.Children.Add(input);
    }

    private static Button CreateButton(Window? owner, string content, string styleKey)
    {
        var button = new Button { Content = content };
        if (owner?.TryFindResource(styleKey) is Style style) button.Style = style;
        return button;
    }
}
