using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.Views;

public partial class RouterView : UserControl
{
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly ObservableCollection<RouterPortSnapshot> _ports = new();
    private CancellationTokenSource? _refreshCancellation;
    private bool _refreshing;

    public RouterView()
    {
        InitializeComponent();
        _routerManagerProvider = ((App)Application.Current).Services.GetRequiredService<IRouterManagerProvider>();
        PortsList.ItemsSource = _ports;
        Loaded += RouterView_Loaded;
        Unloaded += RouterView_Unloaded;
        RouterTabs.SelectionChanged += RouterTabs_SelectionChanged;
    }

    private async void RouterView_Loaded(object sender, RoutedEventArgs e)
    {
        if (RouterTabs.SelectedIndex == 1) await RefreshPortsAsync();
    }

    private void RouterView_Unloaded(object sender, RoutedEventArgs e)
    {
        _refreshCancellation?.Cancel();
    }

    private async void RouterTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == RouterTabs && RouterTabs.SelectedIndex == 1) await RefreshPortsAsync();
    }

    private async Task RefreshPortsAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _refreshCancellation.Token;
        try
        {
            RouterManager manager = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            RouterPortTelemetryResult result = await manager.GetRouterPortTelemetryAsync(cancellationToken);
            RouterManager current = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
            if (!ReferenceEquals(manager, current) || cancellationToken.IsCancellationRequested) return;

            _ports.Clear();
            foreach (RouterPortSnapshot port in result.Ports)
                if (port.IsPhysical || port.InterfaceType == RouterInterfaceType.Unknown)
                    _ports.Add(port);
            PortsStatus.Text = result.Capability == RouterCapabilityState.Supported
                ? $"{_ports.Count} authoritative Ethernet interface(s) found."
                : "Ethernet port telemetry is currently unavailable.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PortsStatus.Text = "Ethernet port telemetry is currently unavailable.";
            System.Diagnostics.Debug.WriteLine($"Router port refresh failed ({exception.GetType().Name}).");
        }
        finally { _refreshing = false; }
    }
}
