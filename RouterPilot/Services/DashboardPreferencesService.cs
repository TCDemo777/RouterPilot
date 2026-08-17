using System.Collections.ObjectModel;
using System.ComponentModel;
using RouterPilot.Models;

namespace RouterPilot.Services;

public sealed class DashboardPreferencesService
{
    private static readonly (string Key, string DisplayName)[] DefaultCards =
    {
        ("router", "Router"),
        ("adguard-home", "AdGuard Home"),
        ("internet", "Internet"),
        ("network-health", "Network Health"),
        ("vpn-status", "VPN")
    };

    private readonly SettingsService _settingsService;
    private bool _suppressPersistence;

    public DashboardPreferencesService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        Cards = new ObservableCollection<DashboardCardPreference>(BuildNormalizedCards(
            _settingsService.Load().DashboardCards));
        NormalizeOrders();

        foreach (DashboardCardPreference card in Cards)
            card.PropertyChanged += Card_PropertyChanged;
    }

    public ObservableCollection<DashboardCardPreference> Cards { get; }

    public event EventHandler? Changed;

    public void MoveUp(DashboardCardPreference? card) => Move(card, -1);

    public void MoveDown(DashboardCardPreference? card) => Move(card, 1);

    public void Reset()
    {
        _suppressPersistence = true;
        try
        {
            Cards.Clear();
            for (int index = 0; index < DefaultCards.Length; index++)
            {
                DashboardCardPreference card = new()
                {
                    Key = DefaultCards[index].Key,
                    DisplayName = DefaultCards[index].DisplayName,
                    IsVisible = true,
                    DisplayOrder = index
                };
                card.PropertyChanged += Card_PropertyChanged;
                Cards.Add(card);
            }
        }
        finally
        {
            _suppressPersistence = false;
        }

        Persist();
    }

    private void Move(DashboardCardPreference? card, int delta)
    {
        if (card is null)
            return;

        int currentIndex = Cards.IndexOf(card);
        int targetIndex = currentIndex + delta;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Cards.Count)
            return;

        _suppressPersistence = true;
        try
        {
            Cards.Move(currentIndex, targetIndex);
            NormalizeOrders();
        }
        finally
        {
            _suppressPersistence = false;
        }

        Persist();
    }

    private void Card_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_suppressPersistence &&
            (e.PropertyName == nameof(DashboardCardPreference.IsVisible) ||
             e.PropertyName == nameof(DashboardCardPreference.DisplayOrder)))
        {
            Persist();
        }
    }

    private void Persist()
    {
        AppSettings settings = _settingsService.Load();
        settings.DashboardCards = Cards
            .Select(card => new DashboardCardPreference
            {
                Key = card.Key,
                DisplayName = card.DisplayName,
                IsVisible = card.IsVisible,
                DisplayOrder = card.DisplayOrder
            })
            .ToList();
        _settingsService.Save(settings);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeOrders()
    {
        for (int index = 0; index < Cards.Count; index++)
            Cards[index].DisplayOrder = index;
    }

    private static IEnumerable<DashboardCardPreference> BuildNormalizedCards(
        IEnumerable<DashboardCardPreference>? savedCards)
    {
        Dictionary<string, DashboardCardPreference> saved = (savedCards ?? [])
            .Where(card => !string.IsNullOrWhiteSpace(card.Key))
            .GroupBy(card => card.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(card => card.DisplayOrder).First(),
                StringComparer.OrdinalIgnoreCase);

        return DefaultCards
            .Select((definition, defaultOrder) =>
            {
                saved.TryGetValue(definition.Key, out DashboardCardPreference? preference);
                return new DashboardCardPreference
                {
                    Key = definition.Key,
                    DisplayName = definition.DisplayName,
                    IsVisible = preference?.IsVisible ?? true,
                    DisplayOrder = preference?.DisplayOrder >= 0
                        ? preference.DisplayOrder
                        : defaultOrder
                };
            })
            .OrderBy(card => card.DisplayOrder)
            .ThenBy(card => card.Key, StringComparer.Ordinal);
    }
}
