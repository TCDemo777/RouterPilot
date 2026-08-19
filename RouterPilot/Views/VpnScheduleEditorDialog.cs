using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RouterPilot.Models;

namespace RouterPilot.Views;

internal static class VpnScheduleEditorDialog
{
    public static void Show(Window? owner, VpnSchedule? existing, Func<VpnSchedule, Task<string?>> saveAsync)
    {
        VpnSchedule source = existing ?? new VpnSchedule { Name = "VPN schedule", Days = ScheduleDays.Weekdays };
        TextBox name = Input(new TextBox { Text = source.Name, MinWidth = 300 });
        TextBox enable = Input(new TextBox { Text = source.EnableTime?.ToString("HH:mm") ?? string.Empty, MinWidth = 110 });
        TextBox disable = Input(new TextBox { Text = source.DisableTime?.ToString("HH:mm") ?? string.Empty, MinWidth = 110 });
        CheckBox enabled = new() { Content = "Schedule enabled", IsChecked = source.IsEnabled };
        CheckBox[] days =
        [
            Day("Mon", ScheduleDays.Monday), Day("Tue", ScheduleDays.Tuesday), Day("Wed", ScheduleDays.Wednesday),
            Day("Thu", ScheduleDays.Thursday), Day("Fri", ScheduleDays.Friday), Day("Sat", ScheduleDays.Saturday), Day("Sun", ScheduleDays.Sunday)
        ];
        foreach (CheckBox checkBox in days) checkBox.IsChecked = (source.Days & (ScheduleDays)checkBox.Tag) != 0;

        TextBlock error = new() { Foreground = owner?.TryFindResource("Brush.Warning") as Brush ?? Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        TextBlock note = new()
        {
            Text = "RouterPilot must remain running for this schedule to work. Minimizing to the system tray keeps schedules active; fully exiting RouterPilot stops them. Missed actions are not replayed after exit or sleep.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");

        Grid form = new() { Margin = new Thickness(20) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 7; i++) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddRow(form, 0, "Schedule name", name);
        AddRow(form, 1, "Enable VPN at", enable);
        AddRow(form, 2, "Disable VPN at", disable);
        AddRow(form, 3, "", enabled);
        WrapPanel dayPanel = new() { Margin = new Thickness(0, 0, 0, 8) };
        foreach (CheckBox day in days) { day.Margin = new Thickness(0, 0, 10, 0); dayPanel.Children.Add(day); }
        AddRow(form, 4, "Days", dayPanel);
        Grid.SetRow(note, 5); Grid.SetColumn(note, 1); form.Children.Add(note);
        Grid.SetRow(error, 6); Grid.SetColumn(error, 1); form.Children.Add(error);

        Button save = Button(owner, "Save", "Button.Primary");
        Button cancel = Button(owner, "Cancel", "Button.Secondary"); cancel.Margin = new Thickness(8, 0, 0, 0);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 20) };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        DockPanel panel = new(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(form);
        Window dialog = new()
        {
            Title = existing is null ? "Add VPN Schedule" : "Edit VPN Schedule", Content = panel, Width = 570,
            MinWidth = 480, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.Surface");
        dialog.SetResourceReference(Window.ForegroundProperty, "Brush.Primary");
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            error.Text = string.Empty;
            if (!TryTime(enable.Text, out TimeOnly? enableTime) || !TryTime(disable.Text, out TimeOnly? disableTime))
            {
                error.Text = "Enter times as HH:mm, or leave an action blank."; return;
            }
            ScheduleDays selected = ScheduleDays.None;
            foreach (CheckBox day in days) if (day.IsChecked == true) selected |= (ScheduleDays)day.Tag;
            VpnSchedule schedule = new()
            {
                Id = source.Id, Name = name.Text.Trim(), IsEnabled = enabled.IsChecked == true, Days = selected,
                EnableTime = enableTime, DisableTime = disableTime, CreatedUtc = source.CreatedUtc,
                UpdatedUtc = source.UpdatedUtc, ExecutedOccurrences = [.. source.ExecutedOccurrences]
            };
            save.IsEnabled = false; cancel.IsEnabled = false;
            string? failure = await saveAsync(schedule);
            if (failure is null) { dialog.DialogResult = true; dialog.Close(); return; }
            error.Text = failure; save.IsEnabled = true; cancel.IsEnabled = true;
        };
        dialog.ShowDialog();
    }

    private static CheckBox Day(string name, ScheduleDays value) => new() { Content = name, Tag = value };
    private static bool TryTime(string text, out TimeOnly? value)
    {
        if (string.IsNullOrWhiteSpace(text)) { value = null; return true; }
        if (TimeOnly.TryParse(text.Trim(), out TimeOnly parsed)) { value = parsed; return true; }
        value = null; return false;
    }
    private static void AddRow(Grid grid, int row, string label, UIElement input)
    {
        if (!string.IsNullOrEmpty(label))
        {
            TextBlock caption = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 8) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary"); Grid.SetRow(caption, row); grid.Children.Add(caption);
        }
        if (input is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
    }
    private static T Input<T>(T input) where T : Control
    {
        input.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceMuted"); input.SetResourceReference(Control.ForegroundProperty, "Brush.Primary"); input.SetResourceReference(Control.BorderBrushProperty, "Brush.BorderStrong"); return input;
    }
    private static Button Button(Window? owner, string content, string style)
    {
        Button button = new() { Content = content }; if (owner?.TryFindResource(style) is Style resource) button.Style = resource; return button;
    }
}
