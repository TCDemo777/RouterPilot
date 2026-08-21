using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using RouterPilot.Services;

namespace RouterPilot.Views
{
    public partial class LoginView : UserControl
    {
        public LoginView()
        {
            InitializeComponent();
        }


        private void Connect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string address = ServerAddressBox.Text.Trim();


                if (string.IsNullOrWhiteSpace(address))
                {
                    ErrorText.Text = "Enter AdGuard address.";
                    return;
                }


                Process.Start(new ProcessStartInfo
                {
                    FileName = address,
                    UseShellExecute = true
                });


                ErrorText.Text = "";
            }
            catch (System.Exception ex)
            {
                ErrorText.Text = OperationFailurePolicy.UserMessage(
                    ex,
                    "Open AdGuard address",
                    "The AdGuard address could not be opened.");
            }
        }


        private void CopyErrorButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(ErrorText.Text))
            {
                Clipboard.SetText(ErrorText.Text);
            }
        }
    }
}
