using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static class BlocklistEditorDialog
{
    public static void Show(Window? owner, string title, AdGuardBlocklist? existing,
        Func<AdGuardBlocklistDraft, Task<string?>> saveAsync)
    {
        var name = new TextBox { Text = existing?.Name ?? string.Empty, MinWidth = 330 };
        var url = new TextBox { Text = existing?.Url ?? string.Empty, MinWidth = 330 };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = existing?.Enabled ?? true };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = owner?.TryFindResource("Brush.Warning") as Brush ?? Brushes.OrangeRed };
        var form = new Grid { Margin = new Thickness(20) };
        for (int row = 0; row < 4; row++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, 0, "Name", name);
        AddRow(form, 1, "URL", url);
        Grid.SetRow(enabled, 2); Grid.SetColumn(enabled, 1); enabled.Margin = new Thickness(0, 0, 0, 8); form.Children.Add(enabled);
        Grid.SetRow(error, 3); Grid.SetColumn(error, 1); form.Children.Add(error);

        Button save = CreateButton(owner, existing is null ? "Add" : "Save", "Button.Primary");
        Button cancel = CreateButton(owner, "Cancel", "Button.Secondary");
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        var dialog = new Window { Title = title, Content = panel, Width = 540, SizeToContent = SizeToContent.Height, MinWidth = 470, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            error.Text = string.Empty; save.IsEnabled = false; cancel.IsEnabled = false;
            string? failure = await saveAsync(new AdGuardBlocklistDraft { Name = name.Text.Trim(), Url = url.Text.Trim(), Enabled = enabled.IsChecked == true });
            if (failure is null) { dialog.DialogResult = true; dialog.Close(); return; }
            error.Text = failure; save.IsEnabled = true; cancel.IsEnabled = true;
        };
        dialog.ShowDialog();
    }

    private static void AddRow(Grid form, int row, string label, Control input)
    {
        var labelBlock = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
        labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        input.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(labelBlock, row); Grid.SetColumn(labelBlock, 0); Grid.SetRow(input, row); Grid.SetColumn(input, 1);
        form.Children.Add(labelBlock); form.Children.Add(input);
    }

    private static Button CreateButton(Window? owner, string content, string styleKey)
    {
        var button = new Button { Content = content };
        if (owner?.TryFindResource(styleKey) is Style style) button.Style = style;
        return button;
    }
}
