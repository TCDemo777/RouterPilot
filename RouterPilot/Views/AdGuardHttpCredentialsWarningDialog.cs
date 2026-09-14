using System.Windows;
using System.Windows.Controls;

namespace RouterPilot.Views;

internal static class AdGuardHttpCredentialsWarningDialog
{
    public static bool Confirm(Window? owner = null)
    {
        var dialog = new Window
        {
            Title = "Unencrypted AdGuard Home connection",
            Width = 520,
            MinWidth = 440,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "Brush.WindowBackground");

        var panel = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock
        {
            Text = "Use dedicated credentials over HTTP?",
            TextWrapping = TextWrapping.Wrap
        };
        title.SetResourceReference(TextBlock.StyleProperty, "Text.SectionTitle");
        panel.Children.Add(title);

        var message = new TextBlock
        {
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Text = "AdGuard Home is configured to use HTTP. Your AdGuard Home username, password, and authenticated control traffic may be visible to other devices on the network.\n\nUse HTTPS where supported."
        };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(message);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 22, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsCancel = true };
        cancel.SetResourceReference(Button.StyleProperty, "Button.Secondary");
        cancel.Click += (_, _) => dialog.DialogResult = false;
        var proceed = new Button { Content = "Continue", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = true };
        proceed.SetResourceReference(Button.StyleProperty, "Button.Primary");
        proceed.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(proceed);
        panel.Children.Add(actions);

        dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }
}
