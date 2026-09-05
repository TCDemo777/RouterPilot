using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;
using Rectangle = System.Windows.Shapes.Rectangle;
using RouterPilot.Models;
using RouterPilot.Services;
using RouterPilot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace RouterPilot.Views
{
    public partial class AboutView : UserControl
    {
        private readonly IRouterManagerProvider _routerManagerProvider;
        private readonly SettingsService _settingsService;
        private readonly UpdateService _updateService;
        private readonly DiagnosticsExecutionService _diagnosticsExecutionService;
        private readonly RouterDiagnosticsToolService _routerDiagnosticsToolService;
        private readonly DiagnosticsHistoryService _diagnosticsHistoryService;

        private readonly StringBuilder _supportLog =
            new StringBuilder();
        private bool _diagnosticsHistorySubscribed;
        private int _logoClickCount;
        private DateTime _logoClickWindowStartedUtc;
        private bool _flightDeckActive;
        private CancellationTokenSource? _flightDeckCancellation;
        private CancellationTokenSource? _autopilotCancellation;
        private LaunchSceneVariation? _launchSceneVariation;
        private int _previousCaptainLogIndex = -1;
        private bool _flightDeckChangelogScrollingEnabled;

        private static readonly string[] CaptainLogs =
        [
            "There are 10 types of people in the world: those who understand binary and those who don't.",
            "The router requested a coffee break. Packets were rerouted to the galley.",
            "Flight computer says the next hop is scenic.",
            "Today's forecast: light winds and a 90% chance of DNS.",
            "The crew has checked the checklist twice. The checklist is pleased.",
            "Packets prefer window seats, especially on long-haul routes.",
            "A good launch has three things: fuel, focus, and a backup gateway.",
            "The moon is out, so the night shift has officially begun.",
            "Our route is clear, our NAT is polite, and our coffee is not.",
            "Never argue with a router that has already chosen its next hop.",
            "The tiny antenna is listening for interesting weather.",
            "Crew note: please do not feed the firewall after midnight.",
            "A packet walked into a bar. The bartender said, ‘Sorry, wrong port.’",
            "The launchpad is level. The network is mostly level.",
            "Every great journey begins with a surprisingly specific subnet.",
            "Tailwinds are good. Tailnets are better.",
            "Control reports all systems nominal, including the snack drawer.",
            "The rocket has a destination. The DNS resolver has opinions.",
            "Today’s mission: reach orbit without touching the guest network.",
            "If found, return this packet to its nearest gateway.",
            "The crew salutes every successful handshake.",
            "A quiet router is a router plotting something interesting.",
            "Ground control approves this route with a small blue light.",
            "The launch window is open. Please keep the firewall closed.",
            "One small hop for a packet, one giant leap for the tailnet.",
            "The best route is the one that arrives with snacks.",
            "No clouds, no collisions, no mysterious captive portals.",
            "The navigation display says: go up, then keep going up.",
            "A little redundancy makes every adventure more relaxing.",
            "The crew has permission to be cautiously optimistic.",
            "This launch is sponsored by stable firmware and strong coffee.",
            "The runway is imaginary, but the packets are real enough.",
            "Please remain seated until the final hop has completed.",
            "The router knows the way. The router will not be taking questions.",
            "A clean route is a beautiful thing.",
            "Control has confirmed: no gremlins detected in the cable tray.",
            "Every star in the sky is just a very distant status light.",
            "The crew packed spare cables and one excellent map.",
            "When in doubt, inspect the gateway and blame the coffee.",
            "The launch sequence is deterministic. The snacks are not.",
            "Our packets are punctual, well-mannered, and slightly aerodynamic.",
            "The network has achieved a comfortable cruising altitude.",
            "A good pilot watches the horizon and the error log.",
            "The signal is strong enough to tell a good story.",
            "Control says the route is clear for whimsical departure.",
            "There is no turbulence, only enthusiastic packet movement.",
            "The crew agrees: this is a very small but respectable space program.",
            "The next stop is somewhere beyond the default gateway.",
            "Remember: every timeout is an opportunity to check the cable.",
            "Captain's note: keep the engines warm and the settings truthful."
        ];

        private sealed record LaunchSceneVariation(
            int CrowdCount,
            IReadOnlyList<Point> CrowdSlots,
            bool VehicleVisible,
            int TreeCount,
            IReadOnlyList<Point> TreeSlots,
            bool IsDark,
            int StarCount,
            int MeteorCount);

        private readonly record struct MeteorPath(Point Start, Point End, double DurationMilliseconds);

        public AboutView()
        {
            InitializeComponent();
            _routerManagerProvider = ((App)Application.Current).Services
                .GetRequiredService<IRouterManagerProvider>();
            _settingsService = ((App)Application.Current).Services
                .GetRequiredService<SettingsService>();
            _updateService = ((App)Application.Current).Services
                .GetRequiredService<UpdateService>();
            _diagnosticsExecutionService = ((App)Application.Current).Services
                .GetRequiredService<DiagnosticsExecutionService>();
            _routerDiagnosticsToolService = ((App)Application.Current).Services
                .GetRequiredService<RouterDiagnosticsToolService>();
            _diagnosticsHistoryService = ((App)Application.Current).Services
                .GetRequiredService<DiagnosticsHistoryService>();
            _diagnosticsExecutionService.LatestResultChanged += DiagnosticsExecution_LatestResultChanged;
            Loaded += AboutView_Loaded;
            Unloaded += AboutView_Unloaded;
            VersionTextBlock.Text = "Version " + GetApplicationVersion();
            BuildDateTextBlock.Text = "Build date: " + GetBuildDate();
            LoadChangelog();
            LoadSystemInformation();
            AppendLog("Support page opened.");
            UpdateReleaseDisplay();
        }

        private void AboutView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_diagnosticsHistorySubscribed)
            {
                _diagnosticsHistoryService.HistoryChanged +=
                    DiagnosticsHistory_CollectionChanged;
                _diagnosticsHistorySubscribed = true;
            }

            RefreshSupportLog();
        }

        private void AboutView_Unloaded(object sender, RoutedEventArgs e)
        {
            ResetFlightDeck();
            if (!_diagnosticsHistorySubscribed)
            {
                return;
            }

            _diagnosticsHistoryService.HistoryChanged -=
                DiagnosticsHistory_CollectionChanged;
            _diagnosticsHistorySubscribed = false;
        }

        private async void RouterPilotLogo_Changed(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_flightDeckActive)
            {
                await ShowAutopilotUnavailableAsync();
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (_logoClickWindowStartedUtc == default ||
                now - _logoClickWindowStartedUtc > TimeSpan.FromSeconds(3))
            {
                _logoClickWindowStartedUtc = now;
                _logoClickCount = 0;
            }

            _logoClickCount++;
            if (_logoClickCount == 7)
                await ActivateFlightDeckAsync();
        }

        private async Task ActivateFlightDeckAsync()
        {
            _flightDeckActive = true;
            _flightDeckCancellation?.Cancel();
            _flightDeckCancellation?.Dispose();
            _flightDeckCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _flightDeckCancellation.Token;
            bool reducedMotion = !SystemParameters.ClientAreaAnimation;

            try
            {
                // Enter the dedicated launch state before hiding its backdrop so
                // normal About content can never flash underneath the fade.
                AboutContentHost.Visibility = Visibility.Collapsed;
                FlightDeckPreflight.Visibility = Visibility.Visible;
                FlightDeckPreflight.BeginAnimation(UIElement.OpacityProperty, null);
                FlightDeckPreflight.Opacity = 1;
                ResetPreflightVisuals();
                _launchSceneVariation = CreateLaunchSceneVariation(IsDarkTheme());
                FlightDeckPreflight.UpdateLayout();
                RenderLaunchScene(_launchSceneVariation);
                Task meteors = !reducedMotion && _launchSceneVariation.MeteorCount > 0
                    ? AnimateMeteorsAsync(_launchSceneVariation.MeteorCount, cancellationToken)
                    : Task.CompletedTask;

                for (int number = 10; number >= 1; number--)
                {
                    await ShowCountdownAsync(number, reducedMotion, cancellationToken);
                    if (number == 9)
                        await RevealCheckAsync(DnsCheckText, reducedMotion, cancellationToken);
                    else if (number == 7)
                        await RevealCheckAsync(RoutingCheckText, reducedMotion, cancellationToken);
                    else if (number == 5)
                        await RevealCheckAsync(PacketsCheckText, reducedMotion, cancellationToken);
                    else if (number == 3)
                    {
                        await RevealCheckAsync(CoffeeCheckText, reducedMotion, cancellationToken);
                        if (!reducedMotion)
                        {
                            await AnimateCoffeeBeatAsync(cancellationToken);
                            PrepareLaunchEffects(3);
                        }
                    }
                    else if (number == 2 && !reducedMotion)
                    {
                        await AnimateCoffeeBeatAsync(cancellationToken);
                        PrepareLaunchEffects(2);
                    }
                }

                // Let the controller's final call disappear before the launch cue.
                CountdownText.BeginAnimation(UIElement.OpacityProperty,
                    reducedMotion ? null : new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
                if (reducedMotion) CountdownText.Opacity = 0;
                await Task.Delay(320, cancellationToken);
                LiftOffText.BeginAnimation(UIElement.OpacityProperty, null);
                LiftOffText.Opacity = reducedMotion ? 1 : 0;
                if (!reducedMotion)
                    LiftOffText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                await Task.Delay(650, cancellationToken);

                await AnimateIgnitionAsync(reducedMotion, cancellationToken);
                await AnimateLogoTakeoffAsync(reducedMotion, cancellationToken);
                FadeResidualSmoke();
                // Let the empty launch scene breathe before the slow direct crossfade.
                await Task.Delay(900, cancellationToken);
                await meteors;
                FlightDeckHost.Visibility = Visibility.Visible;
                PrepareFlightDeckChangelog();
                await CrossfadeToFlightDeckAsync(reducedMotion, cancellationToken);
                SelectCaptainLog();
                StartFlightDeckChangelogScroll(reducedMotion);
                StartAmbientPacketAnimation(reducedMotion);
            }
            catch (OperationCanceledException)
            {
                // Navigation away owns cancellation and resets the transient state.
            }
        }

        private void ResetPreflightVisuals()
        {
            RandomSceneBackground.Children.Clear();
            RandomSceneForeground.Children.Clear();
            CelestialCardLayer.Children.Clear();
            StaticVehicleGroup.Visibility = Visibility.Visible;
            _launchSceneVariation = null;
            CaptainLogText.Text = string.Empty;
            LaunchMotionGroup.BeginAnimation(UIElement.OpacityProperty, null);
            LaunchMotionGroup.Opacity = 1;
            LaunchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            LaunchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            LaunchRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            LaunchTranslation.BeginAnimation(TranslateTransform.XProperty, null);
            LaunchTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            LaunchScale.ScaleX = 1;
            LaunchScale.ScaleY = 1;
            LaunchRotation.Angle = 0;
            LaunchTranslation.X = 0;
            LaunchTranslation.Y = 0;
            CountdownText.BeginAnimation(UIElement.OpacityProperty, null);
            CountdownText.Opacity = 1;
            CountdownText.RenderTransform = new ScaleTransform(1, 1);
            foreach (TextBlock check in new[] { DnsCheckText, RoutingCheckText, PacketsCheckText, CoffeeCheckText })
            {
                check.BeginAnimation(UIElement.OpacityProperty, null);
                check.Opacity = 0;
                check.RenderTransform = new TranslateTransform(0, 8);
            }
            foreach (UIElement effect in new UIElement[] { EngineGlow, ThrustOuter, ThrustInner, ThrustParticleOne, ThrustParticleTwo, ThrustParticleThree })
            {
                effect.BeginAnimation(UIElement.OpacityProperty, null);
                effect.Opacity = 0;
            }
            foreach (UIElement sparkle in new UIElement[] { SparkleOne, SparkleTwo, SparkleThree, SparkleFour, SparkleFive, SparkleSix, SparkleSeven, SparkleEight })
            {
                sparkle.BeginAnimation(UIElement.OpacityProperty, null);
                sparkle.Opacity = 0;
            }
            foreach (UIElement smoke in new[] { SmokeOne, SmokeTwo, SmokeThree, SmokeFour, SmokeFive, SmokeSix })
            {
                smoke.BeginAnimation(UIElement.OpacityProperty, null);
                smoke.Opacity = 0;
                smoke.RenderTransform = new TranslateTransform(0, 0);
            }
            LiftOffText.BeginAnimation(UIElement.OpacityProperty, null);
            LiftOffText.Opacity = 0;
        }

        private static bool IsDarkTheme()
        {
            if (string.Equals(ThemeService.SelectedTheme, ThemeService.DarkTheme, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(ThemeService.SelectedTheme, ThemeService.LightTheme, StringComparison.OrdinalIgnoreCase)) return false;
            return Application.Current.TryFindResource("Brush.WindowBackground") is SolidColorBrush brush
                && brush.Color.R < 128;
        }

        private static LaunchSceneVariation CreateLaunchSceneVariation(bool isDark)
        {
            Point[] crowdSlots =
            [
                new(640, 300), new(680, 285), new(720, 305), new(760, 280),
                new(800, 305), new(840, 282), new(292, 305)
            ];
            Point[] treeSlots = [new(248, 264), new(636, 244), new(856, 246)];
            List<Point> selectedCrowd = SelectSlots(crowdSlots, Random.Shared.Next(2, 8));
            List<Point> selectedTrees = SelectSlots(treeSlots, Random.Shared.Next(0, treeSlots.Length + 1));
            int stars = isDark ? Random.Shared.Next(12, 31) : 0;
            double meteorRoll = Random.Shared.NextDouble();
            int meteors = !isDark ? 0 : meteorRoll < 0.20 ? 0 : meteorRoll < 0.65 ? 1 : meteorRoll < 0.90 ? 2 : 3;
            return new(
                selectedCrowd.Count,
                selectedCrowd,
                Random.Shared.NextDouble() < 0.7,
                selectedTrees.Count,
                selectedTrees,
                isDark,
                stars,
                meteors);
        }

        private static List<Point> SelectSlots(IReadOnlyList<Point> slots, int count)
        {
            List<Point> shuffled = slots.ToList();
            for (int index = shuffled.Count - 1; index > 0; index--)
            {
                int swap = Random.Shared.Next(index + 1);
                (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
            }
            return shuffled.Take(Math.Clamp(count, 0, slots.Count)).ToList();
        }

        private void RenderLaunchScene(LaunchSceneVariation variation)
        {
            StaticVehicleGroup.Visibility = variation.VehicleVisible ? Visibility.Visible : Visibility.Collapsed;
            Brush sky = FindSceneBrush("Brush.WindowBackground", "Brush.SurfaceMuted");
            Brush muted = variation.IsDark ? FindSceneBrush("Brush.TextMuted", "Brush.Border") : SolidColorBrush(0x4E, 0x63, 0x70);
            Brush primary = variation.IsDark ? FindSceneBrush("Brush.TextPrimary", "Brush.TextSecondary") : SolidColorBrush(0x23, 0x35, 0x43);
            Brush accent = FindSceneBrush("Brush.Accent", "Brush.Primary");
            Brush border = variation.IsDark ? FindSceneBrush("Brush.Border", "Brush.TextMuted") : SolidColorBrush(0x51, 0x67, 0x74);
            Brush lightSurface = variation.IsDark ? FindSceneBrush("Brush.SurfaceMuted", "Brush.Surface") : SolidColorBrush(0xD6, 0xE2, 0xE8);
            Brush lightWindow = variation.IsDark ? FindSceneBrush("Brush.Surface", "Brush.SurfaceMuted") : SolidColorBrush(0xF4, 0xF8, 0xFA);
            ApplySceneContrast(variation.IsDark, lightSurface, lightWindow, border, primary, accent);

            if (variation.IsDark)
            {
                CelestialCardLayer.Children.Clear();
                CelestialCardLayer.Children.Add(new Rectangle
                {
                    Width = Math.Max(CelestialCardLayer.ActualWidth, 900),
                    Height = Math.Max(CelestialCardLayer.ActualHeight, 500),
                    Fill = sky, Opacity = 0.42,
                    IsHitTestVisible = false
                });
                double cardWidth = Math.Max(CelestialCardLayer.ActualWidth, 900);
                const double moonSize = 46;
                const double moonPadding = 24;
                double moonLeft = Math.Max(moonPadding, cardWidth - moonSize - moonPadding);
                Ellipse moon = new() { Width = moonSize, Height = moonSize, Fill = primary, Opacity = 0.72 };
                Canvas.SetLeft(moon, moonLeft); Canvas.SetTop(moon, moonPadding); CelestialCardLayer.Children.Add(moon);
                Ellipse moonCutout = new() { Width = 42, Height = 42, Fill = sky, Opacity = 0.95 };
                Canvas.SetLeft(moonCutout, moonLeft + 16); Canvas.SetTop(moonCutout, moonPadding - 7); CelestialCardLayer.Children.Add(moonCutout);
                for (int index = 0; index < variation.StarCount; index++)
                {
                    double width = Math.Max(CelestialCardLayer.ActualWidth, 900);
                    double height = Math.Max(CelestialCardLayer.ActualHeight, 500);
                    Ellipse star = new()
                    {
                        Width = 2 + (index % 3), Height = 2 + (index % 3),
                        Fill = primary, Opacity = 0.38 + (index % 4) * 0.12
                    };
                    Canvas.SetLeft(star, 24 + (index * 137) % Math.Max(30, (int)width - 48));
                    Canvas.SetTop(star, 18 + (index * 83) % Math.Max(30, (int)(height * 0.62)));
                    CelestialCardLayer.Children.Add(star);
                }
            }
            else
            {
                CelestialCardLayer.Children.Clear();
            }

            foreach (Point tree in variation.TreeSlots)
            {
                Polygon canopy = new() { Points = new PointCollection { new(0, 34), new(18, 0), new(36, 34) }, Fill = muted, Opacity = 0.44 };
                Canvas.SetLeft(canopy, tree.X); Canvas.SetTop(canopy, tree.Y); RandomSceneBackground.Children.Add(canopy);
                Rectangle trunk = new() { Width = 6, Height = 18, Fill = muted, Opacity = 0.5 };
                Canvas.SetLeft(trunk, tree.X + 15); Canvas.SetTop(trunk, tree.Y + 29); RandomSceneBackground.Children.Add(trunk);
            }

            foreach ((Point slot, int index) in variation.CrowdSlots.Select((point, index) => (point, index)))
                AddCrewMember(slot, index, primary, accent);
        }

        private void AddCrewMember(Point slot, int index, Brush primary, Brush accent)
        {
            Canvas person = new() { Width = 38, Height = 58, Opacity = 0.82 };
            person.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = primary, Margin = new Thickness(13, 0, 0, 0) });
            person.Children.Add(new Rectangle { Width = 7, Height = 28, Fill = primary, Margin = new Thickness(16, 11, 0, 0) });
            if (index % 3 == 0)
                person.Children.Add(new Line { X1 = 19, Y1 = 21, X2 = 35, Y2 = 12, Stroke = accent, StrokeThickness = 3 });
            else if (index % 3 == 1)
                person.Children.Add(new Rectangle { Width = 12, Height = 8, Fill = accent, Margin = new Thickness(4, 25, 0, 0) });
            else
                person.Children.Add(new Line { X1 = 19, Y1 = 22, X2 = 5, Y2 = 31, Stroke = primary, StrokeThickness = 3 });
            person.Children.Add(new Rectangle { Width = 5, Height = 17, Fill = primary, Margin = new Thickness(11, 38, 0, 0) });
            person.Children.Add(new Rectangle { Width = 5, Height = 17, Fill = primary, Margin = new Thickness(22, 38, 0, 0) });
            Canvas.SetLeft(person, slot.X); Canvas.SetTop(person, slot.Y); RandomSceneForeground.Children.Add(person);
        }

        private async Task AnimateMeteorsAsync(int meteorCount, CancellationToken cancellationToken)
        {
            int[] offsets = meteorCount switch { 1 => [700], 2 => [700, 2200], _ => [700, 2200, 3600] };
            int elapsed = 0;
            for (int index = 0; index < Math.Min(meteorCount, 3); index++)
            {
                int wait = offsets[index] - elapsed;
                if (wait > 0) await Task.Delay(wait, cancellationToken);
                await AnimateMeteorAsync(index, cancellationToken);
                elapsed = offsets[index] + 980;
            }
        }

        private async Task AnimateMeteorAsync(int index, CancellationToken cancellationToken)
        {
            double width = Math.Max(CelestialCardLayer.ActualWidth, 900);
            MeteorPath[] paths =
            [
                new(new(width - 150, 60), new(width * 0.36, 170), 760),
                new(new(80, 76), new(width * 0.52, 132), 760),
                new(new(width * 0.43, 66), new(width - 96, 210), 760),
                new(new(width - 84, 190), new(width * 0.54, 70), 760)
            ];
            MeteorPath path = paths[(Random.Shared.Next(paths.Length) + index) % paths.Length];
            double angle = CalculateMeteorAngle(path.Start, path.End);
            const double headX = 136;
            const double headY = 27;
            Canvas meteor = new() { Width = 150, Height = 54, Opacity = 0, IsHitTestVisible = false };
            meteor.RenderTransformOrigin = new Point(headX / 150, headY / 54);
            meteor.RenderTransform = new RotateTransform(angle);
            Brush primary = FindSceneBrush("Brush.TextPrimary", "Brush.Accent");
            Brush accent = FindSceneBrush("Brush.Accent", "Brush.TextPrimary");
            // The base visual points toward +X: its bright head is at the leading right edge,
            // while each tapered trail layer extends behind it toward -X.
            meteor.Children.Add(new Polygon { Points = new PointCollection { new(4, 27), new(122, 19), new(122, 35) }, Fill = accent, Opacity = 0.22 });
            meteor.Children.Add(new Polygon { Points = new PointCollection { new(12, 27), new(126, 22), new(126, 32) }, Fill = primary, Opacity = 0.7 });
            meteor.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = accent, Opacity = 0.22, Margin = new Thickness(126, 17, 0, 0) });
            meteor.Children.Add(new Polygon { Points = new PointCollection { new(126, 16), new(143, 27), new(126, 38), new(116, 27) }, Fill = primary, Opacity = 0.98 });
            meteor.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(135, 24, 0, 0) });
            Canvas.SetLeft(meteor, path.Start.X - headX); Canvas.SetTop(meteor, path.Start.Y - headY); CelestialCardLayer.Children.Add(meteor);
            meteor.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
            meteor.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(path.Start.X - headX, path.End.X - headX, TimeSpan.FromMilliseconds(path.DurationMilliseconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            meteor.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(path.Start.Y - headY, path.End.Y - headY, TimeSpan.FromMilliseconds(path.DurationMilliseconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            await Task.Delay(TimeSpan.FromMilliseconds(path.DurationMilliseconds), cancellationToken);
            meteor.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
            await Task.Delay(220, cancellationToken);
            CelestialCardLayer.Children.Remove(meteor);
        }

        private static double CalculateMeteorAngle(Point start, Point end)
            => Math.Atan2(end.Y - start.Y, end.X - start.X) * 180 / Math.PI;

        private static Brush FindSceneBrush(string primaryKey, string fallbackKey)
            => (Application.Current.TryFindResource(primaryKey) as Brush)
                ?? (Application.Current.TryFindResource(fallbackKey) as Brush)
                ?? Brushes.White;

        private void PrepareFlightDeckChangelog()
        {
            string? path = FindChangelogPath();
            if (path is null)
            {
                FlightDeckChangelogVersion.Text = "Unavailable";
                FlightDeckChangelogText.Text = "The latest changelog is not available.";
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                int headingIndex = Array.FindIndex(lines, line => line.StartsWith("## ", StringComparison.Ordinal));
                if (headingIndex < 0)
                {
                    FlightDeckChangelogVersion.Text = "Release notes";
                    FlightDeckChangelogText.Text = "No release section was found.";
                    return;
                }

                int end = headingIndex + 1;
                while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal)) end++;
                FlightDeckChangelogVersion.Text = lines[headingIndex][3..].Trim();
                StringBuilder rendered = new();
                for (int index = headingIndex + 1; index < end; index++)
                {
                    string line = lines[index].Trim();
                    if (line.Length == 0)
                    {
                        if (rendered.Length > 0 && rendered[^1] != '\n') rendered.AppendLine();
                    }
                    else if (line.StartsWith("### ", StringComparison.Ordinal)) rendered.AppendLine(line[4..].ToUpperInvariant());
                    else if (line.StartsWith("- ", StringComparison.Ordinal)) rendered.AppendLine("• " + line[2..]);
                    else if (!line.StartsWith("#", StringComparison.Ordinal)) rendered.AppendLine(line);
                }
                FlightDeckChangelogText.Text = rendered.ToString().Trim().Replace("â€¢", "\u2022", StringComparison.Ordinal);
            }
            catch
            {
                FlightDeckChangelogVersion.Text = "Unavailable";
                FlightDeckChangelogText.Text = "The latest changelog could not be read.";
            }
        }

        private static string? FindChangelogPath()
        {
            string[] paths =
            [
                Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "CHANGELOG.md")
            ];
            return paths.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        }

        private void StartFlightDeckChangelogScroll(bool reducedMotion)
        {
            FlightDeckChangelogTranslation.BeginAnimation(TranslateTransform.YProperty, null);
            FlightDeckChangelogTranslation.Y = 0;
            _flightDeckChangelogScrollingEnabled = !reducedMotion;
            if (reducedMotion) return;
            FlightDeckChangelogViewport.UpdateLayout();
            double viewportHeight = FlightDeckChangelogViewport.ActualHeight;
            if (viewportHeight <= 1 || FlightDeckChangelogViewport.ActualWidth <= 1)
            {
                FlightDeckChangelogViewport.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() => StartFlightDeckChangelogScroll(false)));
                return;
            }
            double bodyWidth = Math.Max(1, FlightDeckChangelogViewport.ActualWidth - FlightDeckChangelogText.Margin.Left - FlightDeckChangelogText.Margin.Right);
            FlightDeckChangelogText.Width = bodyWidth;
            FlightDeckChangelogText.Height = double.NaN;
            FlightDeckChangelogText.Measure(new Size(bodyWidth, double.PositiveInfinity));
            double contentHeight = FlightDeckChangelogText.DesiredSize.Height;
            FlightDeckChangelogText.Height = contentHeight;
            FlightDeckChangelogViewport.UpdateLayout();
            double distance = Math.Max(0, contentHeight - viewportHeight);
            if (distance <= 1) return;

            int scrollMilliseconds = Math.Clamp((int)(distance / 22.0 * 1000), 5000, 14000);
            int initialPause = 2000;
            int bottomPause = 2200;
            int returnStart = initialPause + scrollMilliseconds + bottomPause;
            DoubleAnimationUsingKeyFrames animation = new() { RepeatBehavior = RepeatBehavior.Forever };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(initialPause))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(initialPause + scrollMilliseconds))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(returnStart))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(returnStart + scrollMilliseconds))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            FlightDeckChangelogTranslation.BeginAnimation(TranslateTransform.YProperty, animation);
        }

        private void FlightDeckChangelogViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_flightDeckChangelogScrollingEnabled && FlightDeckHost.Visibility == Visibility.Visible && FlightDeckHost.Opacity > 0)
                StartFlightDeckChangelogScroll(false);
        }

        private void ApplySceneContrast(bool dark, Brush surface, Brush window, Brush border, Brush primary, Brush accent)
        {
            LaunchControlBody.Fill = surface;
            LaunchControlBody.Stroke = border;
            LaunchControlRoof.Fill = window;
            LaunchControlRoof.Stroke = border;
            LaunchControlWindowOne.Fill = window;
            LaunchControlWindowTwo.Fill = window;
            LaunchControlWindowThree.Fill = window;
            LaunchControlWindowOne.Stroke = border;
            LaunchControlWindowTwo.Stroke = border;
            LaunchControlWindowThree.Stroke = border;
            LaunchControlDoor.Fill = window;
            LaunchControlDoor.Stroke = border;
            LaunchControlLabel.Foreground = dark ? accent : primary;
            LaunchRoad.Fill = dark ? FindSceneBrush("Brush.SurfaceMuted", "Brush.Border") : SolidColorBrush(0xB8, 0xC6, 0xCD);
            LaunchRoad.Stroke = border;
            LaunchRoadEdge.Fill = dark ? border : SolidColorBrush(0x6C, 0x80, 0x8B);
            SupportVehicleBody.Fill = surface;
            SupportVehicleBody.Stroke = border;
            SupportVehicleCab.Fill = window;
            SupportVehicleCab.Stroke = border;
            SupportVehicleWheelOne.Fill = border;
            SupportVehicleWheelTwo.Fill = border;
            SupportVehicleMark.Foreground = dark ? accent : primary;
        }

        private static SolidColorBrush SolidColorBrush(byte red, byte green, byte blue)
            => new(Color.FromRgb(red, green, blue));

        private void SelectCaptainLog()
        {
            if (CaptainLogs.Length != 50) throw new InvalidOperationException("Captain log pool must contain exactly 50 entries.");
            int candidate;
            do
            {
                candidate = Random.Shared.Next(CaptainLogs.Length);
            }
            while (CaptainLogs.Length > 1 && candidate == _previousCaptainLogIndex);

            _previousCaptainLogIndex = candidate;
            CaptainLogText.Text = CaptainLogs[candidate];
        }

        private async Task ShowCountdownAsync(int number, bool reducedMotion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CountdownText.Text = number.ToString();
            CountdownText.BeginAnimation(UIElement.OpacityProperty, null);
            CountdownText.Opacity = reducedMotion ? 1 : 0;
            if (CountdownText.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1, 1);
                CountdownText.RenderTransform = scale;
            }

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = reducedMotion ? 1 : 1.1;
            scale.ScaleY = reducedMotion ? 1 : 1.1;
            if (!reducedMotion)
            {
                CountdownText.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1.1, 1, TimeSpan.FromMilliseconds(180)));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1.1, 1, TimeSpan.FromMilliseconds(180)));
            }

            await Task.Delay(520, cancellationToken);
        }

        private async Task AnimateIgnitionAsync(bool reducedMotion, CancellationToken cancellationToken)
        {
            if (reducedMotion)
                return;

            EngineGlow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.75, TimeSpan.FromMilliseconds(220)));
            ThrustOuter.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ThrustOuter.Opacity, 0.95, TimeSpan.FromMilliseconds(160)));
            ThrustInner.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ThrustInner.Opacity, 1, TimeSpan.FromMilliseconds(130)));
            ThrustParticleOne.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ThrustParticleOne.Opacity, 0.8, TimeSpan.FromMilliseconds(150)));
            ThrustParticleTwo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ThrustParticleTwo.Opacity, 0.65, TimeSpan.FromMilliseconds(150)));
            ThrustParticleThree.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(ThrustParticleThree.Opacity, 0.75, TimeSpan.FromMilliseconds(150)));
            SparkleOne.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(SparkleOne.Opacity, 0.9, TimeSpan.FromMilliseconds(120)));
            SparkleTwo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(SparkleTwo.Opacity, 0.8, TimeSpan.FromMilliseconds(150)));
            SparkleThree.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(SparkleThree.Opacity, 0.75, TimeSpan.FromMilliseconds(140)));
            SparkleFour.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(SparkleFour.Opacity, 0.85, TimeSpan.FromMilliseconds(160)));
            SparkleFive.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(130)));
            SparkleSix.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(160)));
            SparkleSeven.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.8, TimeSpan.FromMilliseconds(120)));
            SparkleEight.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.75, TimeSpan.FromMilliseconds(150)));
            UIElement[] smoke = [SmokeOne, SmokeTwo, SmokeThree, SmokeFour, SmokeFive, SmokeSix];
            for (int index = 0; index < smoke.Length; index++)
            {
                smoke[index].BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.18 + (index % 3) * 0.06, TimeSpan.FromMilliseconds(180 + index * 25)));
                if (smoke[index].RenderTransform is TranslateTransform drift)
                {
                    drift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, index % 2 == 0 ? -12 : 12, TimeSpan.FromMilliseconds(850)));
                    drift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(850)));
                }
            }
            await Task.Delay(360, cancellationToken);
        }

        private void PrepareLaunchEffects(int stage)
        {
            double glow = stage == 3 ? 0.22 : 0.4;
            ThrustOuter.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(ThrustOuter.Opacity, glow, TimeSpan.FromMilliseconds(180)));
            if (stage == 2)
            {
                SparkleOne.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.55, TimeSpan.FromMilliseconds(180)));
                SparkleTwo.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(210)));
                SparkleThree.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.45, TimeSpan.FromMilliseconds(160)));
                SparkleFour.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(200)));
            }
        }

        private void FadeResidualSmoke()
        {
            foreach (UIElement smoke in new[] { SmokeOne, SmokeTwo, SmokeThree, SmokeFour, SmokeFive, SmokeSix })
                smoke.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(smoke.Opacity, 0, TimeSpan.FromMilliseconds(1100)));
        }

        private static DoubleAnimationUsingKeyFrames Keyframes(double first, double middle, double last, int milliseconds)
        {
            DoubleAnimationUsingKeyFrames animation = new();
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(first, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(middle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds / 2))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(last, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds))));
            return animation;
        }

        private async Task RevealCheckAsync(TextBlock check, bool reducedMotion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            check.Opacity = 0;
            if (check.RenderTransform is not TranslateTransform translation)
            {
                translation = new TranslateTransform(0, 8);
                check.RenderTransform = translation;
            }

            if (reducedMotion)
            {
                check.Opacity = 1;
                translation.Y = 0;
                return;
            }

            check.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)));
            await Task.Delay(270, cancellationToken);
        }

        private async Task AnimateCoffeeBeatAsync(CancellationToken cancellationToken)
        {
            if (CoffeeCheckText.RenderTransform is not ScaleTransform scale)
            {
                scale = new ScaleTransform(1, 1);
                CoffeeCheckText.RenderTransform = scale;
            }

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Keyframes(1, 1.06, 1, 360));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Keyframes(1, 1.06, 1, 360));
            await Task.Delay(390, cancellationToken);
        }

        private Task AnimateLogoTakeoffAsync(bool reducedMotion, CancellationToken cancellationToken)
        {
            if (reducedMotion)
            {
                return FadePreflightForReducedMotionAsync(cancellationToken);
            }

            const int ascentMilliseconds = 3400;
            double exitTranslation = CalculateLaunchExitTranslation();
            LaunchTranslation.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(ascentMilliseconds))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            LaunchTranslation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, exitTranslation, TimeSpan.FromMilliseconds(ascentMilliseconds))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            LaunchRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 5, TimeSpan.FromMilliseconds(ascentMilliseconds)));
            LaunchScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(ascentMilliseconds)));
            LaunchScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.88, TimeSpan.FromMilliseconds(ascentMilliseconds)));
            // Keep the rocket opaque for the entire physical ascent. The
            // clipped launch viewport removes it only after it has exited.
            LaunchMotionGroup.BeginAnimation(UIElement.OpacityProperty, null);
            LaunchMotionGroup.Opacity = 1;
            return Task.Delay(ascentMilliseconds + 80, cancellationToken);
        }

        private double CalculateLaunchExitTranslation()
        {
            const double safetyMargin = 45;
            try
            {
                FlightDeckPreflight.UpdateLayout();
                Point groupOrigin = LaunchMotionGroup.TransformToAncestor(FlightDeckPreflight).Transform(new Point(0, 0));
                Point groupOneUnit = LaunchMotionGroup.TransformToAncestor(FlightDeckPreflight).Transform(new Point(0, 1));
                Point logoBottom = LaunchLogo.TransformToAncestor(FlightDeckPreflight).Transform(new Point(0, LaunchLogo.ActualHeight));
                double renderedScale = Math.Abs(groupOneUnit.Y - groupOrigin.Y);
                if (renderedScale > 0.01)
                    return -(logoBottom.Y + safetyMargin) / renderedScale;
            }
            catch (InvalidOperationException)
            {
                // Layout can be unavailable during cancellation; the measured
                // element dimensions still provide a safe local fallback.
            }

            return -(Canvas.GetTop(LaunchLogo) + Math.Max(LaunchLogo.ActualHeight, 104) + safetyMargin);
        }

        private async Task FadePreflightForReducedMotionAsync(CancellationToken cancellationToken)
        {
            LaunchTranslation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -250, TimeSpan.FromMilliseconds(500)));
            LaunchMotionGroup.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)));
            await Task.Delay(560, cancellationToken);
        }

        private async Task CrossfadeToFlightDeckAsync(bool reducedMotion, CancellationToken cancellationToken)
        {
            if (FlightDeckHost.RenderTransform is not TranslateTransform translation)
                return;

            const int crossfadeMilliseconds = 3000;
            FlightDeckHost.BeginAnimation(UIElement.OpacityProperty, null);
            FlightDeckPreflight.BeginAnimation(UIElement.OpacityProperty, null);
            FlightDeckHost.Opacity = 0;
            FlightDeckPreflight.Opacity = 1;
            translation.BeginAnimation(TranslateTransform.YProperty, null);
            translation.Y = 0;
            FlightDeckHost.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(crossfadeMilliseconds))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            FlightDeckPreflight.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(crossfadeMilliseconds))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } });
            await Task.Delay(crossfadeMilliseconds + 80, cancellationToken);
            FlightDeckPreflight.Visibility = Visibility.Collapsed;
        }

        private void StartAmbientPacketAnimation(bool reducedMotion)
        {
            if (reducedMotion || PacketIndicator.RenderTransform is not TranslateTransform packet)
                return;

            packet.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 310, TimeSpan.FromMilliseconds(2600))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        private async Task ShowAutopilotUnavailableAsync()
        {
            if (AutopilotMessage is null)
                return;

            _autopilotCancellation?.Cancel();
            _autopilotCancellation?.Dispose();
            _autopilotCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = _autopilotCancellation.Token;
            AutopilotMessage.Text = "AUTOPILOT UNAVAILABLE\n\nHave you tried turning the router off and on again?";
            AutopilotMessage.Opacity = 0;
            AutopilotMessage.Visibility = Visibility.Visible;
            if (AutopilotMessage.RenderTransform is TranslateTransform translation)
            {
                translation.Y = 8;
                if (SystemParameters.ClientAreaAnimation)
                {
                    AutopilotMessage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                    translation.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)));
                    await Task.Delay(1800, cancellationToken);
                    AutopilotMessage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
                    await Task.Delay(240, cancellationToken);
                }
                else
                {
                    AutopilotMessage.Opacity = 1;
                    translation.Y = 0;
                    await Task.Delay(900, cancellationToken);
                }
            }
            AutopilotMessage.Visibility = Visibility.Collapsed;
        }

        private void ResetFlightDeck()
        {
            _flightDeckCancellation?.Cancel();
            _flightDeckCancellation?.Dispose();
            _flightDeckCancellation = null;
            _autopilotCancellation?.Cancel();
            _autopilotCancellation?.Dispose();
            _autopilotCancellation = null;
            _logoClickCount = 0;
            _logoClickWindowStartedUtc = default;
            _flightDeckActive = false;
            if (LaunchMotionGroup is not null)
                ResetPreflightVisuals();
            if (RouterPilotLogo is not null)
                RouterPilotLogo.BeginAnimation(OpacityProperty, null);
            if (RouterPilotLogo is not null)
                RouterPilotLogo.Opacity = 1;
            if (FlightDeckPreflight is not null)
            {
                FlightDeckPreflight.BeginAnimation(UIElement.OpacityProperty, null);
                FlightDeckPreflight.Visibility = Visibility.Collapsed;
                FlightDeckPreflight.Opacity = 1;
            }
            if (FlightDeckHost is not null)
            {
                FlightDeckHost.BeginAnimation(UIElement.OpacityProperty, null);
                FlightDeckHost.Visibility = Visibility.Collapsed;
                FlightDeckHost.Opacity = 0;
                if (FlightDeckHost.RenderTransform is TranslateTransform deckTranslation)
                {
                    deckTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                    deckTranslation.Y = 18;
                }
            }
            if (PacketIndicator?.RenderTransform is TranslateTransform packet)
            {
                packet.BeginAnimation(TranslateTransform.XProperty, null);
                packet.X = 0;
            }
            if (FlightDeckChangelogTranslation is not null)
            {
                _flightDeckChangelogScrollingEnabled = false;
                FlightDeckChangelogTranslation.BeginAnimation(TranslateTransform.YProperty, null);
                FlightDeckChangelogTranslation.Y = 0;
                FlightDeckChangelogText.Width = double.NaN;
                FlightDeckChangelogText.Height = double.NaN;
                FlightDeckChangelogText.Text = string.Empty;
                FlightDeckChangelogVersion.Text = string.Empty;
            }
            if (AboutContentHost is not null)
                AboutContentHost.Visibility = Visibility.Visible;
            if (AutopilotMessage is not null)
            {
                AutopilotMessage.BeginAnimation(UIElement.OpacityProperty, null);
                AutopilotMessage.Visibility = Visibility.Collapsed;
                AutopilotMessage.Opacity = 0;
            }
        }

        private void DiagnosticsHistory_CollectionChanged(
            object? sender,
            EventArgs e)
        {
            RefreshSupportLog();
        }

        private void DiagnosticsExecution_LatestResultChanged(object? sender, EventArgs e) =>
            Dispatcher.Invoke(DisplayLatestDiagnosticsResult);

        private void DisplayLatestDiagnosticsResult()
        {
            DiagnosticsExecutionResult? result = _diagnosticsExecutionService.LatestResult;
            if (result is null)
                return;

            DiagnosticsTextBox.Text = result.Outcome == DiagnosticExecutionOutcome.Success &&
                                      !string.IsNullOrWhiteSpace(result.Report)
                ? result.Report
                : result.Message;
            QueryLogWarningBorder.Visibility = result.Report?.Contains("Enabled: False", StringComparison.OrdinalIgnoreCase) == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            LatestVersionTextBlock.Text =
                RouterPilotStatusPresentation.Pending +
                " — checking GitHub Releases...";
            try
            {
                UpdateCheckResult result = await _updateService.CheckForUpdatesAsync(manual: true);
                LatestVersionTextBlock.Text = FormatUpdateCheckResult(result);
                LastUpdateCheckTextBlock.Text = result.CheckedAt is { } checkedAt
                    ? "Last checked: " + checkedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                    : "Last checked: " + RouterPilotStatusPresentation.NotAvailable;
                OpenReleaseNotesButton.IsEnabled = result.LatestRelease?.ReleaseNotesUrl is not null;
            }
            catch (OperationCanceledException)
            {
                LatestVersionTextBlock.Text =
                    RouterPilotStatusPresentation.NotAvailable +
                    " — update check cancelled.";
            }
            finally { CheckForUpdatesButton.IsEnabled = true; }
        }

        private void OpenReleaseNotes_Click(object sender, RoutedEventArgs e)
        {
            string target = _updateService.LatestRelease?.ReleaseNotesUrl?.AbsoluteUri
                ?? UpdateService.ReleasesPageUrl;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        private void UpdateReleaseDisplay()
        {
            AppSettings settings = _settingsService.Load();
            CurrentUpdateVersionTextBlock.Text = "Current version: " + GetApplicationVersion();
            LatestVersionTextBlock.Text = string.IsNullOrWhiteSpace(settings.LatestVersionSeen)
                ? "Latest available version: " + RouterPilotStatusPresentation.NotAvailable
                : "Latest available version: " + settings.LatestVersionSeen;
            LastUpdateCheckTextBlock.Text = settings.LastSuccessfulUpdateCheckUtc is { } last
                ? "Last checked: " + last.ToLocalTime().ToString("dd MMM yyyy HH:mm")
                : "Last checked: " + RouterPilotStatusPresentation.NotAvailable;
            OpenReleaseNotesButton.IsEnabled = !string.IsNullOrWhiteSpace(settings.LatestVersionSeen);
        }

        private static string FormatUpdateCheckResult(UpdateCheckResult result)
        {
            if (result.LatestRelease is not null)
                return "Latest available version: " + result.LatestRelease.Version;

            return result.Status switch
            {
                UpdateCheckStatus.Unavailable =>
                    RouterPilotStatusPresentation.NotAvailable +
                    " — " + result.Message,
                UpdateCheckStatus.Skipped =>
                    RouterPilotStatusPresentation.Pending +
                    " — " + result.Message,
                _ => result.Message
            };
        }

        private async Task RunRouterToolAsync(
            string action,
            Func<RouterManager, string, Task<string>> operation)
        {
            string target =
                DiagnosticTargetBox.Text.Trim();

            DiagnosticsTextBox.Text =
                $"Running {action} for {target} from the router...";

            AppendLog(
                $"{action} requested for {target}.");

            RouterDiagnosticsToolResult result =
                await _routerDiagnosticsToolService.ExecuteAsync(target, operation);

            DiagnosticsTextBox.Text =
                !result.Succeeded
                    ? $"{action} failed ({result.FailureCategory})."
                    : string.IsNullOrWhiteSpace(result.Output)
                    ? $"{action} completed with no output."
                    : result.Output.Trim();

            AppendLog(result.Succeeded
                ? $"{action} completed."
                : $"{action} failed ({result.FailureCategory}).");
        }

        private async void PingTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "Ping",
                (router, target) =>
                    router.PingAsync(target));
        }

        private async void TracerouteTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "Traceroute",
                (router, target) =>
                    router.TracerouteAsync(target));
        }

        private async void DnsLookupTool_Click(
            object sender,
            RoutedEventArgs e)
        {
            await RunRouterToolAsync(
                "DNS lookup",
                (router, target) =>
                    router.DnsLookupAsync(target));
        }

        private async void RunDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            DiagnosticsTextBox.Text =
                "Running diagnostics...";

            DiagnosticsExecutionResult result =
                await _diagnosticsExecutionService.RunAsync(
                    DiagnosticExecutionSource.About);

            if (result.Outcome == DiagnosticExecutionOutcome.Success)
            {
                DiagnosticsTextBox.Text =
                    result.Report;

                QueryLogWarningBorder.Visibility =
                    result.Report!.Contains(
                        "Enabled: False",
                        StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            else
            {
                DiagnosticsTextBox.Text =
                    result.Message;
            }

            RefreshSupportLog();
        }

        private async void EnableQueryLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            EnableQueryLogButton.IsEnabled =
                false;

            EnableQueryLogButton.Content =
                "Enabling...";

            AppendLog("Query-log repair requested.");

            try
            {
                RouterManager routerManager =
                    await GetRouterManagerAsync();

                var current =
                    await routerManager
                        .GetProtectionOptionsAsync();

                await routerManager
                    .SetQueryLogEnabledAsync(
                        true,
                        current);

                ClientRefreshNotifier.RequestRefresh();

                AppendLog(
                    "Query logging enabled; client refresh requested.");

                string report =
                    await routerManager
                        .GetClientDiagnosticsAsync();

                DiagnosticsTextBox.Text =
                    report;

                QueryLogWarningBorder.Visibility =
                    report.Contains(
                        "Enabled: False",
                        StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                DiagnosticsTextBox.Text =
                    "Unable to enable query logging.\n\n" +
                    "Failure category: " +
                    DiagnosticRedactor.FailureCategory(ex);

                QueryLogWarningBorder.Visibility =
                    Visibility.Visible;

                AppendLog(
                    "Query-log repair failed (" +
                    DiagnosticRedactor.FailureCategory(ex) + ").");
            }
            finally
            {
                EnableQueryLogButton.IsEnabled =
                    true;

                EnableQueryLogButton.Content =
                    "Enable query log";
            }
        }

        private void RefreshClients_Click(
            object sender,
            RoutedEventArgs e)
        {
            ClientRefreshNotifier.RequestRefresh();
            AppendLog("Manual client refresh requested.");
        }

        private void CopyDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            CopyText(
                DiagnosticsTextBox.Text,
                "Diagnostics copied.");
        }

        private async void ExportDiagnostics_Click(
            object sender,
            RoutedEventArgs e)
        {
            await BackupDiagnosticsAsync();
        }

        private async void ExportNetworkSnapshot_Click(
            object sender,
            RoutedEventArgs e)
        {
            try
            {
                if (Application.Current.MainWindow?.DataContext is not DashboardViewModel dashboard)
                {
                    DiagnosticsTextBox.Text = "Network snapshot could not be exported.";
                    return;
                }

                NetworkSnapshotExportResult result = await _diagnosticsExecutionService
                    .ExportNetworkSnapshotAsync(dashboard);
                if (!result.Cancelled)
                    DiagnosticsTextBox.Text = result.Message;
                AppendLog(result.Message);
            }
            catch
            {
                // Export must not interrupt Diagnostics or dashboard refresh.
                DiagnosticsTextBox.Text = "Network snapshot could not be exported.";
            }
        }

        private async Task BackupDiagnosticsAsync()
        {
            DiagnosticsExecutionResult result = await _diagnosticsExecutionService.RunAsync(
                DiagnosticExecutionSource.About,
                createBackup: true);
            if (!string.IsNullOrWhiteSpace(result.Report))
                DiagnosticsTextBox.Text = result.Report;
            else if (result.Outcome != DiagnosticExecutionOutcome.Success)
                DiagnosticsTextBox.Text = result.Message;
            RefreshSupportLog();
            DisplayLatestDiagnosticsResult();
        }

        private void RefreshSystem_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadSystemInformation();
            AppendLog("System information refreshed.");
        }

        private void LoadSystemInformation()
        {
            var settings =
                _settingsService.Load();

            long workingSet =
                Environment.WorkingSet;

            var builder =
                new StringBuilder();

            builder.AppendLine("RouterPilot System Information");
            builder.AppendLine(
                "Generated: " +
                DateTimeOffset.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss zzz"));
            builder.AppendLine();

            builder.AppendLine("Application");
            builder.AppendLine("-----------");
            builder.AppendLine("Version: " + GetApplicationVersion());
            builder.AppendLine(
                "Assembly: " +
                (Assembly.GetExecutingAssembly()
                    .GetName()
                    .Version?
                    .ToString() ?? "unknown"));
            builder.AppendLine(
                "Process architecture: " +
                RuntimeInformation.ProcessArchitecture);
            builder.AppendLine(
                "Memory usage: " +
                FormatBytes(
                    workingSet));
            builder.AppendLine();

            builder.AppendLine("Runtime");
            builder.AppendLine("-------");
            builder.AppendLine(
                ".NET: " +
                RuntimeInformation.FrameworkDescription);
            builder.AppendLine(
                "OS: " +
                RuntimeInformation.OSDescription);
            builder.AppendLine(
                "OS architecture: " +
                RuntimeInformation.OSArchitecture);
            builder.AppendLine(
                "64-bit process: " +
                Environment.Is64BitProcess);
            builder.AppendLine(
                "Processor count: " +
                Environment.ProcessorCount);
            builder.AppendLine();

            builder.AppendLine("Configured router");
            builder.AppendLine("-----------------");
            builder.AppendLine(
                "Address: " +
                settings.RouterHost);
            builder.AppendLine(
                "Username: " +
                settings.Username);
            builder.AppendLine(
                "Refresh interval: " +
                settings.RefreshIntervalSeconds +
                " seconds");
            builder.AppendLine(
                "Password stored: " +
                (!string.IsNullOrWhiteSpace(
                    settings.EncryptedPassword)));

            SystemTextBox.Text =
                builder.ToString();
        }

        private static string GetBuildInformation()
        {
            var assembly =
                Assembly.GetExecutingAssembly();

            return
                "RouterPilot v" + GetApplicationVersion() + "\n" +
                "Assembly version: " +
                (assembly.GetName().Version?.ToString() ?? "unknown") +
                "\nBuild location: " +
                AppContext.BaseDirectory +
                "\nGenerated: " +
                DateTimeOffset.Now.ToString("O");
        }


        private static string GetBuildDate()
        {
            try
            {
                string location = Assembly.GetExecutingAssembly().Location;

                if (!string.IsNullOrWhiteSpace(location) &&
                    File.Exists(location))
                {
                    return File.GetLastWriteTime(location)
                        .ToString("dd MMM yyyy");
                }
            }
            catch
            {
                // A build date is helpful metadata, but failure to read it
                // must never prevent the About page from loading.
            }

            return "unknown";
        }


        private static string GetApplicationVersion()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();

            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataIndex = informationalVersion.IndexOf('+');
                return metadataIndex >= 0
                    ? informationalVersion[..metadataIndex]
                    : informationalVersion;
            }

            Version? version = assembly.GetName().Version;
            return version is null
                ? "unknown"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        private static string FormatBytes(long bytes)
        {
            string[] suffixes =
            {
                "B",
                "KB",
                "MB",
                "GB"
            };

            double value =
                bytes;

            int index = 0;

            while (value >= 1024 &&
                   index < suffixes.Length - 1)
            {
                value /= 1024;
                index++;
            }

            return
                $"{value:F1} {suffixes[index]}";
        }

        private void CopyLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            CopyText(
                GetSupportLogText(),
                "Support log copied.");
        }

        private async void ClearLog_Click(
            object sender,
            RoutedEventArgs e)
        {
            _supportLog.Clear();
            await _diagnosticsHistoryService.ClearAsync();
            AppendLog("Support log cleared.");
        }

        private void CopyText(
            string text,
            string successMessage)
        {
            if (string.IsNullOrWhiteSpace(
                    text))
            {
                return;
            }

            Clipboard.SetText(
                text);

            AppendLog(
                successMessage);
        }

        private void AppendLog(string message)
        {
            _supportLog.AppendLine(
                $"[{DateTime.Now:HH:mm:ss}] {message}");

            RefreshSupportLog();
        }

        private string GetSupportLogText()
        {
            string diagnosticsLog = _diagnosticsHistoryService.GetLogText();
            if (string.IsNullOrWhiteSpace(diagnosticsLog))
            {
                return _supportLog.ToString();
            }

            return _supportLog + diagnosticsLog + Environment.NewLine;
        }

        private void RefreshSupportLog()
        {
            // Support history remains available to its dedicated Logs page.
            // About no longer hosts a duplicate output control.
        }

        private void LoadChangelog()
        {
            string[] candidatePaths =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "CHANGELOG.md"),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "CHANGELOG.md")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath =
                    Path.GetFullPath(
                        path);

                if (!File.Exists(
                        fullPath))
                {
                    continue;
                }

                try
                {
                    ChangelogTextBox.Text =
                        File.ReadAllText(
                            fullPath,
                            Encoding.UTF8);

                    return;
                }
                catch (Exception ex)
                {
                    ChangelogTextBox.Text =
                        OperationFailurePolicy.UserMessage(
                            ex,
                            "Changelog read",
                            "The changelog could not be read.");

                    return;
                }
            }

            ChangelogTextBox.Text =
                "CHANGELOG.md was not found.";
        }

        private void ReloadChangelog_Click(
            object sender,
            RoutedEventArgs e)
        {
            LoadChangelog();
            AppendLog("Changelog reloaded.");
        }

        private void OpenLicense_Click(
            object sender,
            RoutedEventArgs e)
        {
            string[] candidatePaths =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "LICENSE"),
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "LICENSE")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath =
                    Path.GetFullPath(
                        path);

                if (!File.Exists(
                        fullPath))
                {
                    continue;
                }

                Process.Start(
                    new ProcessStartInfo(
                        fullPath)
                    {
                        UseShellExecute = true
                    });

                AppendLog(
                    "Licence opened.");

                return;
            }

            MessageBox.Show(
                "The LICENSE file could not be found.",
                "Licence",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void OpenThirdPartyNotices_Click(object sender, RoutedEventArgs e)
        {
            string[] candidatePaths =
            {
                Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "THIRD_PARTY_NOTICES.txt")
            };

            foreach (string path in candidatePaths)
            {
                string fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    continue;

                Process.Start(new ProcessStartInfo(fullPath) { UseShellExecute = true });
                AppendLog("Third-party notices opened.");
                return;
            }

            MessageBox.Show("The third-party notices file could not be found.", "Third-Party Notices",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void SupportDevelopment_Click(
            object sender,
            RoutedEventArgs e)
        {
            const string sponsorsUrl =
                "https://github.com/sponsors/TCDemo777";

            AppendLog(
                "Opening GitHub Sponsors page...");

            OpenExternalUrl(sponsorsUrl);
        }

        private Task<RouterManager> GetRouterManagerAsync() =>
            _routerManagerProvider.GetRouterManagerAsync();

        private void BuyMeACoffee_Click(
            object sender,
            RoutedEventArgs e)
        {
            const string buyMeACoffeeUrl =
                "https://buymeacoffee.com/tcdemo777";

            AppendLog(
                "Opening Buy Me a Coffee page...");

            OpenExternalUrl(buyMeACoffeeUrl);
        }

        private static void OpenExternalUrl(string url)
        {
            Process.Start(
                new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
        }

        private void GitHubLink_RequestNavigate(
            object sender,
            RequestNavigateEventArgs e)
        {
            OpenExternalUrl(e.Uri.AbsoluteUri);

            e.Handled = true;
        }
    }
}
