using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RouterPilot.Controls;

public partial class StatisticCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata("—"));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(
            nameof(Subtitle),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata(string.Empty, OnOptionalTextChanged));

    public static readonly DependencyProperty ValueForegroundProperty =
        DependencyProperty.Register(
            nameof(ValueForeground),
            typeof(Brush),
            typeof(StatisticCard),
            new PropertyMetadata(null));

    /// <summary>Uses the compact, single-line status-value treatment for state labels.</summary>
    public static readonly DependencyProperty UseCompactStatusValueProperty =
        DependencyProperty.Register(
            nameof(UseCompactStatusValue),
            typeof(bool),
            typeof(StatisticCard),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IconGlyphProperty =
        DependencyProperty.Register(
            nameof(IconGlyph),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata(string.Empty, OnIconGlyphChanged));

    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(StatisticCard),
            new PropertyMetadata(Brushes.RoyalBlue));

    public static readonly DependencyProperty IconBackgroundProperty =
        DependencyProperty.Register(
            nameof(IconBackground),
            typeof(Brush),
            typeof(StatisticCard),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(239, 246, 255))));

    public static readonly DependencyProperty BadgeTextProperty =
        DependencyProperty.Register(
            nameof(BadgeText),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata(string.Empty, OnBadgeTextChanged));

    public static readonly DependencyProperty BadgeBackgroundProperty =
        DependencyProperty.Register(
            nameof(BadgeBackground),
            typeof(Brush),
            typeof(StatisticCard),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(240, 253, 244))));

    public static readonly DependencyProperty BadgeForegroundProperty =
        DependencyProperty.Register(
            nameof(BadgeForeground),
            typeof(Brush),
            typeof(StatisticCard),
            new PropertyMetadata(new SolidColorBrush(Color.FromRgb(21, 128, 61))));

    public static readonly DependencyProperty FooterTextProperty =
        DependencyProperty.Register(
            nameof(FooterText),
            typeof(string),
            typeof(StatisticCard),
            new PropertyMetadata(string.Empty, OnFooterTextChanged));

    private static readonly DependencyPropertyKey SubtitleVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(SubtitleVisibility),
            typeof(Visibility),
            typeof(StatisticCard),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty SubtitleVisibilityProperty =
        SubtitleVisibilityPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey IconVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IconVisibility),
            typeof(Visibility),
            typeof(StatisticCard),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty IconVisibilityProperty =
        IconVisibilityPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey BadgeVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(BadgeVisibility),
            typeof(Visibility),
            typeof(StatisticCard),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty BadgeVisibilityProperty =
        BadgeVisibilityPropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey FooterVisibilityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(FooterVisibility),
            typeof(Visibility),
            typeof(StatisticCard),
            new PropertyMetadata(Visibility.Collapsed));

    public static readonly DependencyProperty FooterVisibilityProperty =
        FooterVisibilityPropertyKey.DependencyProperty;

    public StatisticCard()
    {
        InitializeComponent();
        UpdateOptionalVisibilities();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public Brush ValueForeground
    {
        get => (Brush)GetValue(ValueForegroundProperty);
        set => SetValue(ValueForegroundProperty, value);
    }

    public bool UseCompactStatusValue
    {
        get => (bool)GetValue(UseCompactStatusValueProperty);
        set => SetValue(UseCompactStatusValueProperty, value);
    }

    public string IconGlyph
    {
        get => (string)GetValue(IconGlyphProperty);
        set => SetValue(IconGlyphProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush IconBackground
    {
        get => (Brush)GetValue(IconBackgroundProperty);
        set => SetValue(IconBackgroundProperty, value);
    }

    public string BadgeText
    {
        get => (string)GetValue(BadgeTextProperty);
        set => SetValue(BadgeTextProperty, value);
    }

    public Brush BadgeBackground
    {
        get => (Brush)GetValue(BadgeBackgroundProperty);
        set => SetValue(BadgeBackgroundProperty, value);
    }

    public Brush BadgeForeground
    {
        get => (Brush)GetValue(BadgeForegroundProperty);
        set => SetValue(BadgeForegroundProperty, value);
    }

    public string FooterText
    {
        get => (string)GetValue(FooterTextProperty);
        set => SetValue(FooterTextProperty, value);
    }

    public Visibility SubtitleVisibility =>
        (Visibility)GetValue(SubtitleVisibilityProperty);

    public Visibility IconVisibility =>
        (Visibility)GetValue(IconVisibilityProperty);

    public Visibility BadgeVisibility =>
        (Visibility)GetValue(BadgeVisibilityProperty);

    public Visibility FooterVisibility =>
        (Visibility)GetValue(FooterVisibilityProperty);

    private static void OnOptionalTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((StatisticCard)dependencyObject).SetValue(
            SubtitleVisibilityPropertyKey,
            HasText(args.NewValue) ? Visibility.Visible : Visibility.Collapsed);
    }

    private static void OnIconGlyphChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((StatisticCard)dependencyObject).SetValue(
            IconVisibilityPropertyKey,
            HasText(args.NewValue) ? Visibility.Visible : Visibility.Collapsed);
    }

    private static void OnBadgeTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((StatisticCard)dependencyObject).SetValue(
            BadgeVisibilityPropertyKey,
            HasText(args.NewValue) ? Visibility.Visible : Visibility.Collapsed);
    }

    private static void OnFooterTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        ((StatisticCard)dependencyObject).SetValue(
            FooterVisibilityPropertyKey,
            HasText(args.NewValue) ? Visibility.Visible : Visibility.Collapsed);
    }

    private void UpdateOptionalVisibilities()
    {
        SetValue(
            SubtitleVisibilityPropertyKey,
            HasText(Subtitle) ? Visibility.Visible : Visibility.Collapsed);
        SetValue(
            IconVisibilityPropertyKey,
            HasText(IconGlyph) ? Visibility.Visible : Visibility.Collapsed);
        SetValue(
            BadgeVisibilityPropertyKey,
            HasText(BadgeText) ? Visibility.Visible : Visibility.Collapsed);
        SetValue(
            FooterVisibilityPropertyKey,
            HasText(FooterText) ? Visibility.Visible : Visibility.Collapsed);
    }

    private static bool HasText(object? value) =>
        value is string text && !string.IsNullOrWhiteSpace(text);
}
