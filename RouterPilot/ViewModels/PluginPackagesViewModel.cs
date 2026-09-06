using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouterPilot.Models;
using RouterPilot.Services;

namespace RouterPilot.ViewModels;

public partial class PluginPackagesViewModel : ObservableObject
{
    private readonly IPluginPackageService _service;
    private readonly IPluginPackageMutationService _mutation;
    private readonly IRouterProfileService _profiles;

    private CancellationTokenSource? _loadCancellation;
    private long _generation;

    public ObservableCollection<PluginPackage> Packages { get; } = new();

    public ICollectionView PackagesView { get; }

    public IReadOnlyList<string> FilterOptions { get; } =
    [
        "All",
        "Installed",
        "Available",
        "Updates"
    ];

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string selectedFilter = "All";

    [ObservableProperty]
    private PluginPackage? selectedPackage;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool isOperating;

    [ObservableProperty]
    private string statusMessage = "Plug-ins have not been loaded.";

    [ObservableProperty]
    private string indexStatus = "Unknown";

    [ObservableProperty]
    private string indexFreshness = "Freshness unknown";

    [ObservableProperty]
    private int installedCount;

    [ObservableProperty]
    private int availableCount;

    [ObservableProperty]
    private string updatesCount = "—";

    public string EmptyMessage =>
        IsLoading
            ? "Loading plug-ins…"
            : Packages.Count == 0
                ? "Package information is unavailable."
                : "No packages match your search.";

    public bool CanInstall =>
        !IsOperating &&
        SelectedPackage?.CanInstall == true;

    public bool CanRemove =>
        !IsOperating &&
        SelectedPackage?.CanRemove == true;

    public bool CanUpdate =>
        !IsOperating &&
        SelectedPackage?.CanUpdate == true;

    public bool CanForceUpdate =>
        !IsOperating &&
        SelectedPackage?.CanForceUpdate == true;

    public bool ShouldShowUninstall =>
        SelectedPackage?.ShouldShowUninstall == true;

    public bool ShouldShowUpdate =>
        SelectedPackage?.ShouldShowUpdate == true;

    public bool CanRefreshIndexes =>
        !IsOperating;

    public string ActionBlockReason =>
        SelectedPackage is { } package &&
        (
            (ShouldShowUninstall && !CanRemove) ||
            (ShouldShowUpdate && !CanUpdate)
        )
            ? package.MutationSafetyReason
            : string.Empty;

    public bool HasActionBlockReason =>
        !string.IsNullOrWhiteSpace(ActionBlockReason);

    public string UpdateBlockReason =>
        ShouldShowUpdate && !CanUpdate
            ? DescribeBlockedAction("updates")
            : string.Empty;

    public string UninstallBlockReason =>
        ShouldShowUninstall && !CanRemove
            ? DescribeBlockedAction("uninstalling")
            : string.Empty;

    public string ForceUpdateBlockReason =>
        ShouldShowUpdate && !CanForceUpdate
            ? SelectedPackage?.UpdateProtection switch
            {
                PluginUpdateProtection.HardBlockedCritical =>
                    "RouterPilot blocks advanced updates for this critical system package.",

                PluginUpdateProtection.Safe =>
                    "This package can be updated normally; an advanced update override is not required.",

                _ =>
                    "Advanced update override is not available for this package."
            }
            : string.Empty;

    public PluginPackagesViewModel(
        IPluginPackageService service,
        IPluginPackageMutationService mutation,
        IRouterProfileService profiles)
    {
        _service = service;
        _mutation = mutation;
        _profiles = profiles;

        _profiles.ActiveProfileChanged += (_, _) => _ = RefreshAsync();

        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        PackagesView.Filter = FilterPackage;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        long generation = Interlocked.Increment(ref _generation);
        string? selectedName = SelectedPackage?.Name;

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();

        _loadCancellation =
            new CancellationTokenSource(TimeSpan.FromSeconds(60));

        IsLoading = true;
        StatusMessage = "Loading read-only plug-in inventory…";

        try
        {
            PluginInventorySnapshot snapshot =
                await _service
                    .LoadAsync(_loadCancellation.Token)
                    .ConfigureAwait(true);

            if (generation != Interlocked.Read(ref _generation))
                return;

            Packages.Clear();

            foreach (PluginPackage package in snapshot.Packages)
                Packages.Add(package);

            SelectedPackage =
                selectedName is null
                    ? null
                    : Packages.FirstOrDefault(
                        package =>
                            string.Equals(
                                package.Name,
                                selectedName,
                                StringComparison.OrdinalIgnoreCase));

            InstalledCount = snapshot.InstalledCount;
            AvailableCount = snapshot.AvailableCount;
            UpdatesCount =
                snapshot.UpgradableCount?.ToString("N0") ?? "—";

            IndexStatus = snapshot.InventoryStatus switch
            {
                PluginInventoryAvailability.Available => "Available",
                PluginInventoryAvailability.Stale => "Stale",
                PluginInventoryAvailability.Unavailable => "Unavailable",
                _ => "Unknown"
            };

            IndexFreshness =
                snapshot.IndexFreshness == PluginInventoryFreshness.Known
                    ? "Freshness known"
                    : "Freshness unknown";

            StatusMessage =
                snapshot.InstalledError ??
                snapshot.AvailableError ??
                snapshot.UpgradableError ??
                $"Read {Packages.Count:N0} packages from the router.";

            PackagesView.Refresh();
        }
        catch (OperationCanceledException)
            when (_loadCancellation?.IsCancellationRequested == true)
        {
            StatusMessage = "Plug-in inventory refresh cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage =
                OperationFailurePolicy.UserMessage(
                    ex,
                    "Plug-in inventory",
                    "Plug-in information is currently unavailable.");

            IndexStatus = "Unavailable";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(EmptyMessage));
            NotifyActionState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() =>
        MutateAsync(
            "install",
            "Installing…",
            "Installed successfully.");

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() =>
        MutateAsync(
            "remove",
            "Uninstalling…",
            "Uninstalled successfully.");

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync() =>
        MutateAsync(
            "update-package",
            "Updating…",
            "Updated successfully.");

    [RelayCommand(CanExecute = nameof(CanForceUpdate))]
    private Task ForceUpdateAsync() =>
        MutateAsync(
            "force-update-package",
            "Applying advanced update…",
            "Updated successfully.");

    [RelayCommand(CanExecute = nameof(CanRefreshIndexes))]
    private Task RefreshIndexesAsync() =>
        MutateAsync(
            "update-indexes",
            "Refreshing package indexes…",
            "Package indexes refreshed.");

    private async Task MutateAsync(
        string operation,
        string working,
        string success)
    {
        PluginPackage? selected = SelectedPackage;

        if (operation != "update-indexes" && selected is null)
            return;

        if (operation == "install" && !CanInstall)
            return;

        if (operation == "remove" && !CanRemove)
            return;

        if (operation == "update-package" && !CanUpdate)
            return;

        if (operation == "force-update-package" && !CanForceUpdate)
            return;

        if (operation == "update-indexes" && !CanRefreshIndexes)
            return;

        if (selected is not null &&
            operation != "update-indexes" &&
            !ConfirmMutation(operation, selected))
        {
            return;
        }

        IsOperating = true;
        StatusMessage = working;
        NotifyActionState();

        try
        {
            await _mutation.ExecuteAsync(
                operation,
                selected?.Name ?? string.Empty);

            StatusMessage = success;

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Verifying package state…";

            await RefreshAsync();

            StatusMessage =
                OperationFailurePolicy.UserMessage(
                    ex,
                    "Plug-in operation",
                    "The package operation could not be completed; the refreshed package state is authoritative.");
        }
        finally
        {
            IsOperating = false;
            NotifyActionState();
        }
    }

    partial void OnSelectedPackageChanged(PluginPackage? value)
    {
        NotifyActionState();
    }

    partial void OnIsOperatingChanged(bool value)
    {
        NotifyActionState();
    }

    partial void OnSearchTextChanged(string value)
    {
        PackagesView.Refresh();
    }

    partial void OnSelectedFilterChanged(string value)
    {
        PackagesView.Refresh();
    }

    private void NotifyActionState()
    {
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanUpdate));
        OnPropertyChanged(nameof(CanForceUpdate));

        OnPropertyChanged(nameof(ShouldShowUninstall));
        OnPropertyChanged(nameof(ShouldShowUpdate));

        OnPropertyChanged(nameof(CanRefreshIndexes));

        OnPropertyChanged(nameof(ActionBlockReason));
        OnPropertyChanged(nameof(HasActionBlockReason));

        OnPropertyChanged(nameof(UpdateBlockReason));
        OnPropertyChanged(nameof(UninstallBlockReason));
        OnPropertyChanged(nameof(ForceUpdateBlockReason));

        InstallCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        ForceUpdateCommand.NotifyCanExecuteChanged();
        RefreshIndexesCommand.NotifyCanExecuteChanged();
    }

    private string DescribeBlockedAction(string action) =>
        SelectedPackage?.MutationSafety switch
        {
            PluginMutationSafety.BlockedSystem =>
                $"RouterPilot blocks {action} this protected system package.",

            PluginMutationSafety.BlockedDependencyRisk =>
                $"RouterPilot blocks {action} because dependency safety is not established.",

            _ =>
                $"RouterPilot cannot {action} this package safely."
        };

    private static bool ConfirmMutation(
        string operation,
        PluginPackage package)
    {
        if (operation == "force-update-package")
            return ConfirmAdvancedPackageUpdate(package);

        string action = operation switch
        {
            "install" => "Install",
            "remove" => "Uninstall",
            "update-package" => "Update",
            _ => "Change"
        };

        string detail =
            operation == "update-package"
                ? $"\n\nInstalled: {package.DisplayInstalledVersion}\nAvailable: {package.DisplayAvailableVersion}"
                : "\n\nThis changes software installed on the router.";

        return MessageBox.Show(
                   $"{action} {package.Name}?{detail}",
                   $"{action} plug-in",
                   MessageBoxButton.YesNo,
                   operation == "remove"
                       ? MessageBoxImage.Warning
                       : MessageBoxImage.Question)
               == MessageBoxResult.Yes;
    }

    private static bool ConfirmAdvancedPackageUpdate(
        PluginPackage package)
    {
        bool confirmed = false;

        Window dialog = new()
        {
            Title = "Advanced package update",
            Width = 620,
            Height = 610,
            MinWidth = 560,
            MinHeight = 560,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        if (Application.Current?.MainWindow is { } mainWindow &&
            !ReferenceEquals(mainWindow, dialog))
        {
            dialog.Owner = mainWindow;
        }

        Grid root = new()
        {
            Margin = new Thickness(24)
        };

        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });

        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star)
        });

        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });

        TextBlock heading = new()
        {
            Text = "Advanced package update",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        };

        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        ScrollViewer scrollViewer = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        Grid.SetRow(scrollViewer, 1);

        StackPanel content = new();

        content.Children.Add(
            new TextBlock
            {
                Text = $"Package: {package.Name}",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            });

        content.Children.Add(
            new TextBlock
            {
                Text = $"Installed: {package.DisplayInstalledVersion}",
                Margin = new Thickness(0, 0, 0, 4)
            });

        content.Children.Add(
            new TextBlock
            {
                Text = $"Available: {package.DisplayAvailableVersion}",
                Margin = new Thickness(0, 0, 0, 18)
            });

        Border warningBorder = new()
        {
            Padding = new Thickness(14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 18)
        };

        warningBorder.Child =
            new TextBlock
            {
                Text =
                    "RouterPilot has classified this as a protected system package.\n\n" +
                    "Updating it outside the GL.iNet firmware upgrade process may break router services, networking, the GL.iNet interface, or make the router inaccessible.\n\n" +
                    "This action is not recommended.\n\n" +
                    "RouterPilot cannot guarantee compatibility, stability, or recovery.\n\n" +
                    "Recovery may require SSH, LuCI, firmware recovery, or a factory reset.\n\n" +
                    "Proceed entirely at your own risk.",
                TextWrapping = TextWrapping.Wrap
            };

        content.Children.Add(warningBorder);

        CheckBox acknowledgement = new()
        {
            Content =
                "I understand that this update may make the router unstable or inaccessible " +
                "and that RouterPilot cannot guarantee recovery.",
            Margin = new Thickness(0, 0, 0, 18)
        };

        content.Children.Add(acknowledgement);

        content.Children.Add(
            new TextBlock
            {
                Text = "Type the package name to confirm:",
                Margin = new Thickness(0, 0, 0, 6)
            });

        TextBox packageConfirmation = new()
        {
            MinHeight = 34,
            Padding = new Thickness(8, 5, 8, 5)
        };

        content.Children.Add(packageConfirmation);

        scrollViewer.Content = content;
        root.Children.Add(scrollViewer);

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };

        Button cancelButton = new()
        {
            Content = "Cancel",
            Padding = new Thickness(16, 7, 16, 7),
            MinWidth = 90,
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true
        };

        Button forceButton = new()
        {
            Content = "Force update",
            Padding = new Thickness(16, 7, 16, 7),
            MinWidth = 110,
            IsEnabled = false
        };

        void UpdateConfirmationState()
        {
            forceButton.IsEnabled =
                acknowledgement.IsChecked == true &&
                string.Equals(
                    packageConfirmation.Text,
                    package.Name,
                    StringComparison.Ordinal);
        }

        acknowledgement.Checked += (_, _) =>
            UpdateConfirmationState();

        acknowledgement.Unchecked += (_, _) =>
            UpdateConfirmationState();

        packageConfirmation.TextChanged += (_, _) =>
            UpdateConfirmationState();

        cancelButton.Click += (_, _) =>
        {
            confirmed = false;
            dialog.DialogResult = false;
        };

        forceButton.Click += (_, _) =>
        {
            if (!forceButton.IsEnabled)
                return;

            confirmed = true;
            dialog.DialogResult = true;
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(forceButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;

        bool? result = dialog.ShowDialog();

        return result == true && confirmed;
    }

    private bool FilterPackage(object item)
    {
        if (item is not PluginPackage package)
            return false;

        string search = SearchText.Trim();

        bool matchesSearch =
            string.IsNullOrWhiteSpace(search) ||
            package.Name.Contains(
                search,
                StringComparison.OrdinalIgnoreCase) ||
            package.Description.Contains(
                search,
                StringComparison.OrdinalIgnoreCase);

        bool matchesFilter =
            SelectedFilter switch
            {
                "Installed" => package.IsInstalled,
                "Available" => package.IsAvailable,
                "Updates" => package.IsUpgradable,
                _ => true
            };

        return matchesSearch && matchesFilter;
    }
}