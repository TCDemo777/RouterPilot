using RouterPilot.Models;
using RouterPilot.Presentation;
using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using RouterPilot.ViewModels;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
NetworkHealthViewInput Input(DataFreshnessState router = DataFreshnessState.Fresh, DataFreshnessState wan = DataFreshnessState.Fresh, DataFreshnessState adGuardFreshness = DataFreshnessState.Fresh, DataFreshnessState wifi = DataFreshnessState.Fresh, DataFreshnessState dhcp = DataFreshnessState.Fresh, AdGuardAvailabilityState adGuard = AdGuardAvailabilityState.Available, string vpn = "Connected", bool statsLoaded = true, RouterPilotStatus stats = RouterPilotStatus.Active, string cpu = "10%", string temperature = "45 C", string memory = "40%", string storage = "20%", string uptime = "1d", string load = "0.1") => new(router, wan, adGuardFreshness, DataFreshnessState.Fresh, wifi, dhcp, true, true, "now", "1.2.3.4", "192.168.1.1", "1.1.1.1", adGuard, true, true, false, true, true, vpn, "WireGuard", 2, 2, 0, 0, 3, true, 3, 1, cpu, temperature, memory, storage, uptime, load, "1.0", FirmwareUpdateCheckStatus.UpToDate, statsLoaded, stats, "Existing status.");
NetworkHealthViewSnapshot healthy = NetworkHealthViewProjection.Create(Input());
Require(healthy.OverallStatus == "Healthy", "healthy state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Unavailable)).OverallStatus == "Unavailable", "router unavailable");
NetworkHealthViewSnapshot adGuardUnavailable = NetworkHealthViewProjection.Create(Input(adGuard: AdGuardAvailabilityState.Unavailable));
Require(adGuardUnavailable.Checks.Single(x => x.Title == "DNS / AdGuard").Status == "Unavailable" && adGuardUnavailable.OverallStatus == "Attention needed", "AdGuard unavailable");
Require(NetworkHealthViewProjection.Create(Input(vpn: "Disconnected")).Checks.Single(x => x.Title == "VPN").Status == "Disconnected", "VPN disconnected");
Require(NetworkHealthViewProjection.Create(Input(stats: RouterPilotStatus.Disabled)).Checks.Single(x => x.Title == "Data Statistics").Status == "Disabled", "statistics disabled");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Stale)).Checks.Single(x => x.Title == "Router").Status == "Stale", "stale state");
Require(NetworkHealthViewProjection.Create(Input(statsLoaded: false)).Checks.Single(x => x.Title == "Data Statistics").Status == "Not loaded", "partial state");
Require(NetworkHealthViewProjection.Create(Input(DataFreshnessState.Loading)).OverallStatus == "Initializing", "loading state");
Require(NetworkHealthViewProjection.Create(Input(wifi: DataFreshnessState.Loading)).OverallStatus != "Healthy", "Wi-Fi loading state");
Require(NetworkHealthViewProjection.Create(Input(dhcp: DataFreshnessState.Loading)).OverallStatus != "Healthy", "DHCP loading state");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "-", memory: "-", storage: "-", uptime: "-", load: "-")).Checks.Single(x => x.Title == "Router resources").Status == "Unavailable", "missing resources");
Require(NetworkHealthViewProjection.Create(Input(cpu: "-", temperature: "45 C")).Checks.Single(x => x.Title == "Router resources").Status == "Partial", "partial resources");
Require(NetworkHealthViewProjection.Create(Input(wan: DataFreshnessState.Loading)).OverallStatus != "Healthy", "WAN loading state");
Require(NetworkHealthViewProjection.Create(Input(adGuardFreshness: DataFreshnessState.Loading)).OverallStatus != "Healthy", "AdGuard loading state");
using ServiceProvider services = new ServiceCollection().AddSingleton<DashboardViewModel>().BuildServiceProvider();
Require(ReferenceEquals(services.GetRequiredService<DashboardViewModel>(), services.GetRequiredService<DashboardViewModel>()), "Dashboard ViewModel DI registration must be authoritative.");
Console.WriteLine("Network Health projection fixtures passed: 15/15.");
