using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static class BlocklistEditorDialog
{
    public static void Show(Window? owner, string title, AdGuardBlocklist? existing,
        Func<AdGuardBlocklistDraft, Task<string?>> saveAsync)
    {
        var name = CreateInput(existing?.Name ?? string.Empty);
        var url = CreateInput(existing?.Url ?? string.Empty);
        var enabled = new CheckBox { Content = "Enabled", IsChecked = existing?.Enabled ?? true };
        enabled.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        var form = new Grid { Margin = new Thickness(20, 18, 20, 10) };
        for (int row = 0; row < 4; row++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddRow(form, 0, "Name", name);
        AddRow(form, 1, "URL", url);
        Grid.SetRow(enabled, 2); Grid.SetColumn(enabled, 1); enabled.Margin = new Thickness(0, 0, 0, 8); form.Children.Add(enabled);
        Grid.SetRow(error, 3); Grid.SetColumn(error, 1); form.Children.Add(error);

        Button save = CreateButton(existing is null ? "Add" : "Save", "Button.Primary");
        Button cancel = CreateButton("Cancel", "Button.Secondary");
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 18) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        panel.SetResourceReference(Panel.BackgroundProperty, "Brush.Surface");
        panel.SetResourceReference(TextElement.ForegroundProperty, "Brush.TextPrimary");
        var dialog = new Window { Title = title, Content = panel, Width = 480, SizeToContent = SizeToContent.Height, MinWidth = 420, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        dialog.SetResourceReference(Window.ForegroundProperty, "Brush.TextPrimary");
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

    private static TextBox CreateInput(string text)
    {
        var input = new TextBox { Text = text, MinWidth = 260, Padding = new Thickness(10, 5, 10, 5) };
        input.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceMuted");
        input.SetResourceReference(Control.ForegroundProperty, "Brush.TextPrimary");
        input.SetResourceReference(Control.BorderBrushProperty, "Brush.BorderStrong");
        input.SetResourceReference(TextBox.CaretBrushProperty, "Brush.TextPrimary");
        input.SetResourceReference(TextBox.SelectionBrushProperty, "Brush.Primary");
        return input;
    }

    private static Button CreateButton(string content, string styleKey)
    {
        var button = new Button { Content = content };
        button.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return button;
    }
}
