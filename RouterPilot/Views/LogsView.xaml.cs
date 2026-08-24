using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace RouterPilot.Views
{
    public partial class LogsView : UserControl
    {
        private readonly LogsViewModel _viewModel;
        private bool _isActive;

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
            IsVisibleChanged += LogsView_IsVisibleChanged;
        }

        private async void LogsView_Loaded(
            object sender,
            RoutedEventArgs e)
        {
            await ActivateAsync();
        }

        private void LogsView_Unloaded(
            object sender,
            RoutedEventArgs e)
        {
            Deactivate();
        }

        private async void LogsView_IsVisibleChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (!IsLoaded) return;

            if (IsVisible)
                await ActivateAsync();
            else
                Deactivate();
        }

        private async Task ActivateAsync()
        {
            if (_isActive || !IsVisible) return;

            _isActive = true;
            await _viewModel.StartAsync();

            if (!_isActive)
                _viewModel.Stop();
        }

        private void Deactivate()
        {
            _isActive = false;
            _viewModel.Stop();
        }

        public void ApplyDomainFilter(
            string domain) =>
            _viewModel.ApplyDomainFilter(domain);
    }
}
