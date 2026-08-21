using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class NewDeviceNotificationTracker
{
    private readonly NotificationService _notificationService;
    private readonly object _syncRoot = new();
    private readonly HashSet<string> _seenMacAddresses =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _previouslyConnected =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _baselineEstablished;

    public NewDeviceNotificationTracker(NotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    public async Task ProcessAsync(IEnumerable<ClientInfo> connectedClients)
    {
        ArgumentNullException.ThrowIfNull(connectedClients);

        Dictionary<string, ClientInfo> current = connectedClients
            .Select(client => (Mac: ClientIdentity.NormalizeMac(client.MacAddress), Client: client))
            .Where(item => item.Mac.Length == 12)
            .GroupBy(item => item.Mac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Client,
                StringComparer.OrdinalIgnoreCase);

        List<(string Mac, ClientInfo Client)> newDevices;

        lock (_syncRoot)
        {
            if (!_baselineEstablished)
            {
                _seenMacAddresses.UnionWith(current.Keys);
                _previouslyConnected = current.Keys.ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
                _baselineEstablished = true;
                return;
            }

            newDevices = current
                .Where(item =>
                    !_previouslyConnected.Contains(item.Key) &&
                    _seenMacAddresses.Add(item.Key))
                .Select(item => (item.Key, item.Value))
                .ToList();

            _previouslyConnected = current.Keys.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        }

        foreach ((string mac, ClientInfo client) in newDevices)
        {
            string deviceName = FirstUsefulValue(
                client.Name,
                client.RouterName,
                client.MacAddress);
            string networkName = FirstUsefulValue(
                client.WifiNetwork,
                client.ConnectionType,
                "network");
            string ipSuffix = HasUsefulValue(client.IpAddress)
                ? $" (IP: {client.IpAddress})"
                : string.Empty;

            await _notificationService.AddAsync(new AppNotification
            {
                Title = "New Device",
                Message = $"{deviceName} joined {networkName}{ipSuffix}",
                Severity = NotificationSeverity.Information,
                Category = NotificationCategory.Device,
                EventType = NotificationEventType.NewDeviceDetected,
                DeduplicationKey = $"NewDevice:{mac}"
            });
        }
    }

    private static string FirstUsefulValue(params string?[] values) =>
        values.First(HasUsefulValue)!;

    private static bool HasUsefulValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value != "-" &&
        !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown device", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Unknown network", StringComparison.OrdinalIgnoreCase);

}
