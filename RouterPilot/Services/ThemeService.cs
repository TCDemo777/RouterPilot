using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RouterPilot.Services
{
    public static class ThemeService
    {
        public const string SystemTheme = "System";
        public const string LightTheme = "Light";
        public const string DarkTheme = "Dark";

        private const string LightThemePath = "Themes/LightTheme.xaml";
        private const string DarkThemePath = "Themes/DarkTheme.xaml";

        private static bool _initialized;
        private static string _selectedTheme = SystemTheme;

        public static string SelectedTheme => _selectedTheme;

        public static bool IsDarkActive =>
            _selectedTheme == DarkTheme ||
            (_selectedTheme == SystemTheme && IsWindowsDarkTheme());

        public static void Initialize(string? selectedTheme)
        {
            _selectedTheme = Normalize(selectedTheme);

            if (!_initialized)
            {
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                _initialized = true;
            }

            ApplySelectedTheme();
        }

        public static void Apply(string? selectedTheme)
        {
            _selectedTheme = Normalize(selectedTheme);
            ApplySelectedTheme();
        }

        public static string Normalize(string? selectedTheme)
        {
            if (string.Equals(selectedTheme, LightTheme, StringComparison.OrdinalIgnoreCase))
            {
                return LightTheme;
            }

            if (string.Equals(selectedTheme, DarkTheme, StringComparison.OrdinalIgnoreCase))
            {
                return DarkTheme;
            }

            return SystemTheme;
        }

        private static void ApplySelectedTheme()
        {
            ApplyPalette(IsDarkActive ? DarkThemePath : LightThemePath);
        }

        private static void ApplyPalette(string palettePath)
        {
            Application? application = Application.Current;
            if (application is null)
            {
                return;
            }

            void ApplyOnUiThread()
            {
                ResourceDictionary? activePalette =
                    application.Resources.MergedDictionaries.FirstOrDefault(
                        dictionary =>
                            dictionary.Source is not null &&
                            (dictionary.Source.OriginalString.EndsWith(
                                 LightThemePath,
                                 StringComparison.OrdinalIgnoreCase) ||
                             dictionary.Source.OriginalString.EndsWith(
                                 DarkThemePath,
                                 StringComparison.OrdinalIgnoreCase)));

                if (activePalette is null)
                {
                    activePalette = new ResourceDictionary
                    {
                        Source = new Uri(LightThemePath, UriKind.Relative)
                    };

                    application.Resources.MergedDictionaries.Insert(0, activePalette);
                }

                var targetPalette = new ResourceDictionary
                {
                    Source = new Uri(palettePath, UriKind.Relative)
                };

                // Mutating existing brushes preserves references created by
                // StaticResource in older views, so the whole application updates.
                foreach (object key in targetPalette.Keys)
                {
                    object targetValue = targetPalette[key];

                    if (targetValue is SolidColorBrush targetBrush &&
                        activePalette[key] is SolidColorBrush activeBrush)
                    {
                        if (activeBrush.IsFrozen)
                        {
                            activePalette[key] = targetBrush.Clone();
                        }
                        else
                        {
                            activeBrush.Color = targetBrush.Color;
                            activeBrush.Opacity = targetBrush.Opacity;
                        }

                        continue;
                    }

                    activePalette[key] = targetValue;
                }
            }

            if (application.Dispatcher.CheckAccess())
            {
                ApplyOnUiThread();
            }
            else
            {
                application.Dispatcher.Invoke(ApplyOnUiThread);
            }
        }

        private static bool IsWindowsDarkTheme()
        {
            try
            {
                object? value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    1);

                return value is int lightThemeEnabled && lightThemeEnabled == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void OnUserPreferenceChanged(
            object sender,
            UserPreferenceChangedEventArgs e)
        {
            if (_selectedTheme == SystemTheme)
            {
                ApplySelectedTheme();
            }
        }
    }
}
