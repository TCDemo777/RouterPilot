using System.Windows;
using System.Windows.Controls;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class LogsView : UserControl
    {
        private readonly LogsViewModel _viewModel;

        public LogsView()
        {
            InitializeComponent();

            _viewModel =
                ((App)Application.Current).Services
                    .GetRequiredService<LogsViewModel>();

            DataContext =
                _viewModel;

            Loaded +=
                LogsView_Loaded;

            Unloaded +=
                LogsView_Unloaded;
        }

        private async void LogsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            await _viewModel
                .StartAsync();
        }

        private void LogsView_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            _viewModel.Stop();
        }

        public void ApplyDomainFilter(
            string domain) =>
            _viewModel.ApplyDomainFilter(domain);
    }
}
