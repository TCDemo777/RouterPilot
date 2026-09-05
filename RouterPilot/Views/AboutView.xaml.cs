using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        private bool _changelogScrollPreparing;
        private bool _changelogScrollRestartPending;
        private int _changelogScrollStartCount;
        private double _lastChangelogViewportWidth;
        private double _lastChangelogViewportHeight;
        private readonly TranslateTransform _changelogTranslate = new();

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
            ChangelogDocument.RenderTransform = _changelogTranslate;
            Debug.WriteLine("CHANGELOG_TRANSFORM_CREATED=TranslateTransform");
            Debug.WriteLine("CHANGELOG_TRANSFORM_ASSIGNED=ChangelogDocument.RenderTransform");
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
                AboutSurface.UpdateLayout();
                FlightDeckHost.Height = AboutSurface.ActualHeight > 100
                    ? Math.Clamp(AboutSurface.ActualHeight - 40, 480, 620)
                    : 620;
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
                FlightDeckRoot.Visibility = Visibility.Visible;
                FlightDeckHost.Visibility = Visibility.Visible;
                PrepareFlightDeckChangelog();
                await CrossfadeToFlightDeckAsync(reducedMotion, cancellationToken);
                UpdateFlightDeckFootnoteVisibility();
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
            => ThemeService.IsDarkActive;

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

            RenderCelestialTheme(variation, sky, primary, accent);

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

        private void RenderCelestialTheme(LaunchSceneVariation variation, Brush sky, Brush primary, Brush accent)
        {
            CelestialCardLayer.Children.Clear();
            double width = CelestialCardLayer.ActualWidth > 1 ? CelestialCardLayer.ActualWidth : 900;
            double height = CelestialCardLayer.ActualHeight > 1 ? CelestialCardLayer.ActualHeight : 500;
            Rectangle background = new()
            {
                Width = width,
                Height = height,
                Fill = variation.IsDark ? sky : Brushes.Transparent,
                Opacity = variation.IsDark ? 0.42 : 1,
                IsHitTestVisible = false
            };
            Panel.SetZIndex(background, 0);
            CelestialCardLayer.Children.Add(background);

            const double padding = 24;
            if (variation.IsDark)
            {
                Ellipse moon = new()
                {
                    Width = 62,
                    Height = 62,
                    Fill = new SolidColorBrush(Color.FromRgb(0xF5, 0xF1, 0xD0)),
                    Opacity = 0.98,
                    IsHitTestVisible = false
                };
                Point moonPosition = PlaceCelestialBody(width, height, 62, 62, padding, upperRight: true);
                Canvas.SetLeft(moon, moonPosition.X); Canvas.SetTop(moon, moonPosition.Y);
                Panel.SetZIndex(moon, 20);
                CelestialCardLayer.Children.Add(moon);

                Ellipse moonCutout = new()
                {
                    Width = 56,
                    Height = 56,
                    Fill = sky,
                    Opacity = 0.98,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(moonCutout, moonPosition.X + 21);
                Canvas.SetTop(moonCutout, moonPosition.Y - 6);
                Panel.SetZIndex(moonCutout, 21);
                CelestialCardLayer.Children.Add(moonCutout);

                for (int index = 0; index < variation.StarCount; index++)
                {
                    Ellipse star = new()
                    {
                        Width = 2 + (index % 3), Height = 2 + (index % 3),
                        Fill = primary, Opacity = 0.38 + (index % 4) * 0.12,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(star, padding + (index * 137) % Math.Max(30, (int)width - 48));
                    Canvas.SetTop(star, 18 + (index * 83) % Math.Max(30, (int)(height * 0.62)));
                    Panel.SetZIndex(star, 10);
                    CelestialCardLayer.Children.Add(star);
                }
            }
            else
            {
                Point sunPosition = PlaceCelestialBody(width, height, 58, 58, padding, upperRight: true);
                Ellipse sunGlow = new()
                {
                    Width = 82, Height = 82,
                    Fill = new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xC8, 0x4A)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(sunGlow, sunPosition.X - 12); Canvas.SetTop(sunGlow, sunPosition.Y - 12);
                Panel.SetZIndex(sunGlow, 10); CelestialCardLayer.Children.Add(sunGlow);
                Ellipse sun = new()
                {
                    Width = 58, Height = 58,
                    Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x4A)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0x92, 0x2E)),
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(sun, sunPosition.X); Canvas.SetTop(sun, sunPosition.Y);
                Panel.SetZIndex(sun, 20); CelestialCardLayer.Children.Add(sun);
                for (int ray = 0; ray < 8; ray++)
                {
                    double angle = ray * Math.PI / 4;
                    double centerX = sunPosition.X + 29;
                    double centerY = sunPosition.Y + 29;
                    Line line = new()
                    {
                        X1 = centerX + Math.Cos(angle) * 36,
                        Y1 = centerY + Math.Sin(angle) * 36,
                        X2 = centerX + Math.Cos(angle) * 44,
                        Y2 = centerY + Math.Sin(angle) * 44,
                        Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0x92, 0x2E)),
                        StrokeThickness = 2,
                        IsHitTestVisible = false
                    };
                    Panel.SetZIndex(line, 20); CelestialCardLayer.Children.Add(line);
                }
            }
        }

        private static Point PlaceCelestialBody(double cardWidth, double cardHeight, double bodyWidth, double bodyHeight, double padding, bool upperRight)
        {
            double x = upperRight ? cardWidth - bodyWidth - padding : padding;
            double y = padding;
            return new(
                Math.Clamp(double.IsFinite(x) ? x : padding, padding, Math.Max(padding, cardWidth - bodyWidth - padding)),
                Math.Clamp(double.IsFinite(y) ? y : padding, padding, Math.Max(padding, cardHeight - bodyHeight - padding)));
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
            (string source, string content)? source = ReadCanonicalChangelog();
            if (source is null)
            {
                FlightDeckChangelogVersion.Text = "Unavailable";
                FlightDeckChangelogText.Text = "The latest changelog is not available.";
                return;
            }

            try
            {
                string normalized = NormalizeChangelogText(source.Value.content);
                string[] lines = normalized.Split('\n');
                int firstMeaningfulIndex = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
                if (firstMeaningfulIndex < 0)
                {
                    FlightDeckChangelogVersion.Text = "Release notes";
                    FlightDeckChangelogText.Text = "No release section was found.";
                    return;
                }

                int latestReleaseIndex = Array.FindIndex(lines, firstMeaningfulIndex, lines.Length - firstMeaningfulIndex,
                    line => line.StartsWith("## ", StringComparison.Ordinal)
                        && !line[3..].Trim().Equals("Unreleased", StringComparison.OrdinalIgnoreCase));
                FlightDeckChangelogVersion.Text = latestReleaseIndex >= 0 ? lines[latestReleaseIndex][3..].Trim() : "Release notes";
                RecordChangelogTextDiagnostics("ASSIGNED", normalized, source.Value.source);
                FlightDeckChangelogText.Text = normalized;
                Debug.WriteLine($"CHANGELOG_SOURCE={source.Value.source}");
                Debug.WriteLine($"CHANGELOG_SELECTED_VERSION={FlightDeckChangelogVersion.Text}");
                Debug.WriteLine($"CHANGELOG_RELEASE_SECTIONS={lines.Count(line => line.StartsWith("## ", StringComparison.Ordinal))}");
                Debug.WriteLine($"CHANGELOG_TOTAL_LINES={lines.Length - firstMeaningfulIndex}");
                Debug.WriteLine($"CHANGELOG_TOTAL_CHARS={source.Value.content.Length}");
                Debug.WriteLine($"CHANGELOG_FIRST_MEANINGFUL={lines[firstMeaningfulIndex].Trim()}");
                int lastMeaningfulIndex = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
                Debug.WriteLine($"CHANGELOG_LAST_MEANINGFUL={(lastMeaningfulIndex >= 0 ? lines[lastMeaningfulIndex].Trim() : "none")}");
                StringBuilder rendered = new();
                for (int index = firstMeaningfulIndex; index < lines.Length; index++)
                {
                    string line = lines[index].Trim();
                    if (line.Length == 0)
                    {
                        if (rendered.Length > 0 && rendered[^1] != '\n') rendered.AppendLine();
                    }
                    else if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        if (rendered.Length > 0) rendered.AppendLine();
                        rendered.AppendLine(line[3..].Trim());
                    }
                    else if (line.StartsWith("### ", StringComparison.Ordinal)) rendered.AppendLine(line[4..].ToUpperInvariant());
                    else if (line.StartsWith("- ", StringComparison.Ordinal)) rendered.AppendLine("• " + line[2..]);
                    else if (line.StartsWith("#", StringComparison.Ordinal)) rendered.AppendLine(line.TrimStart('#', ' '));
                    else rendered.AppendLine(line);
                }
                // The Flight Deck body is the canonical source text itself.
                // Keep formatting-only reconstruction out of the displayed
                // content so no historical lines can be lost.
                FlightDeckChangelogText.Text = normalized;
                Debug.WriteLine($"CHANGELOG_SOURCE_NORMALIZED_CHARS={normalized.Length}");
                Debug.WriteLine($"CHANGELOG_RENDERED_CHARS={FlightDeckChangelogText.Text.Length}");
                Debug.WriteLine($"CHANGELOG_LOADED_LINE_COUNT={normalized.Split('\n').Length}");
                Debug.WriteLine($"CHANGELOG_LOADED_CHAR_COUNT={source.Value.content.Length}");
                int loadedLastMeaningfulIndex = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
                Debug.WriteLine($"CHANGELOG_LOADED_FINAL_MEANINGFUL={(loadedLastMeaningfulIndex >= 0 ? lines[loadedLastMeaningfulIndex].Trim() : "none")}");
                Debug.WriteLine($"CHANGELOG_CONTENT_EQUAL={string.Equals(normalized, FlightDeckChangelogText.Text, StringComparison.Ordinal)}");
#if DEBUG
                FlightDeckChangelogDebugLabel.Text = $"SOURCE: {source.Value.source} · LINES: {normalized.Split('\n').Length} · HASH: {Sha256(normalized)[..8]}";
#endif
            }
            catch
            {
                FlightDeckChangelogVersion.Text = "Unavailable";
                FlightDeckChangelogText.Text = "The latest changelog could not be read.";
            }
        }

        private static string NormalizeChangelogText(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        private static (string source, string content)? ReadCanonicalChangelog()
        {
            Assembly assembly = typeof(AboutView).Assembly;
            List<string> manifestNames = assembly.GetManifestResourceNames().ToList();
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith("CHANGELOG.md", StringComparison.OrdinalIgnoreCase));
            WriteChangelogDiagnostic($"PROCESS_EXE_PATH={Environment.ProcessPath}");
            WriteChangelogDiagnostic($"ASSEMBLY_LOCATION={assembly.Location}");
            WriteChangelogDiagnostic($"ASSEMBLY_VERSION={assembly.GetName().Version}");
            WriteChangelogDiagnostic($"INFORMATIONAL_VERSION={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
            WriteChangelogDiagnostic("RESOURCE_LOOKUP_ATTEMPTED=CHANGELOG.md");
            WriteChangelogDiagnostic($"RESOURCE_FOUND={(resourceName is not null ? "YES" : "NO")}");
            WriteChangelogDiagnostic($"RESOURCE_NAME={resourceName ?? "<none>"}");
            foreach (string name in manifestNames.Where(name => name.Contains("CHANGELOG", StringComparison.OrdinalIgnoreCase)))
                WriteChangelogDiagnostic($"MANIFEST_CHANGELOG_RESOURCE={name}");
            if (resourceName is not null)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string content = reader.ReadToEnd();
                    WriteChangelogDiagnostic($"ACTUAL_SOURCE_USED=EMBEDDED");
                    WriteChangelogDiagnostic($"RESOURCE_LENGTH={content.Length}");
                    WriteChangelogDiagnostic($"RESOURCE_SHA256={Sha256(NormalizeChangelogText(content))}");
                    return ($"embedded:{resourceName}", content);
                }
            }

            string? path = FindChangelogPath();
            WriteChangelogDiagnostic($"EXTERNAL_CHANGELOG_PATH_ATTEMPTED={path ?? "<none>"}");
            WriteChangelogDiagnostic($"EXTERNAL_CHANGELOG_EXISTS={(path is not null ? "YES" : "NO")}");
            return path is null ? null : (path, File.ReadAllText(path, Encoding.UTF8));
        }

        private static void RecordChangelogTextDiagnostics(string stage, string text, string source)
        {
            string normalized = NormalizeChangelogText(text);
            string[] lines = normalized.Split('\n');
            int first = Array.FindIndex(lines, line => !string.IsNullOrWhiteSpace(line));
            int last = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
            WriteChangelogDiagnostic($"{stage}_SOURCE={source}");
            WriteChangelogDiagnostic($"{stage}_CHAR_COUNT={text.Length}");
            WriteChangelogDiagnostic($"{stage}_LINE_COUNT={lines.Length}");
            WriteChangelogDiagnostic($"{stage}_SHA256={Sha256(normalized)}");
            WriteChangelogDiagnostic($"{stage}_FIRST_MEANINGFUL_LINE={(first >= 0 ? lines[first].Trim() : "none")}");
            WriteChangelogDiagnostic($"{stage}_FINAL_MEANINGFUL_LINE={(last >= 0 ? lines[last].Trim() : "none")}");
        }

        private static string Sha256(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

        private static void WriteChangelogDiagnostic(string line)
        {
            Debug.WriteLine(line);
#if DEBUG
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "flightdeck-loader-debug.txt");
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
#endif
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
            if (!_flightDeckActive || _changelogScrollPreparing)
                return;

            _changelogScrollPreparing = true;
            try
            {
            _changelogTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            _changelogTranslate.Y = 0;
            Debug.WriteLine("CHANGELOG_TRANSFORM_Y_SET=0");
            // The changelog is a readability feature, not launch decoration.
            // Keep it moving even when reduced-motion suppresses the scene's
            // ornamental animations.
            _flightDeckChangelogScrollingEnabled = true;
            FlightDeckChangelogViewport.UpdateLayout();
            RecordChangelogTextDiagnostics("VISIBLE_TEXT", FlightDeckChangelogText.Text, "FlightDeckChangelogText.Text");
            double viewportHeight = FlightDeckChangelogViewport.ActualHeight;
            Debug.WriteLine($"CHANGELOG_SCROLL_LAYOUT_READY viewport={viewportHeight:0.##} width={FlightDeckChangelogViewport.ActualWidth:0.##}");
            if (viewportHeight <= 1 || FlightDeckChangelogViewport.ActualWidth <= 1)
            {
                _ = FlightDeckChangelogViewport.Dispatcher.BeginInvoke(
                    DispatcherPriority.Render,
                    new Action(() => StartFlightDeckChangelogScroll(false)));
                return;
            }
            const double horizontalPadding = 12;
            double bodyWidth = Math.Max(1, FlightDeckChangelogViewport.ActualWidth);
            double textWidth = Math.Max(1, bodyWidth - horizontalPadding * 2);
            FlightDeckChangelogText.Width = textWidth;
            FlightDeckChangelogText.Height = double.NaN;
            FlightDeckChangelogText.Measure(new Size(textWidth, double.PositiveInfinity));
            double contentHeight = FlightDeckChangelogText.DesiredSize.Height;
            ChangelogDocument.Width = bodyWidth;
            ChangelogDocument.Height = contentHeight;
            FlightDeckChangelogText.Arrange(new Rect(0, 0, textWidth, contentHeight));
            FlightDeckChangelogViewport.UpdateLayout();
            const double bottomPadding = 16;
            UIElement? lastContent = ChangelogDocument.Children
                .OfType<UIElement>()
                .LastOrDefault(element => element.Visibility == Visibility.Visible);
            double lastContentBottom = contentHeight;
            if (lastContent is not null)
            {
                double renderedHeight = lastContent is FrameworkElement frameworkElement && frameworkElement.ActualHeight > 0
                    ? frameworkElement.ActualHeight
                    : lastContent.DesiredSize.Height;
                try
                {
                    Point bottom = lastContent.TransformToAncestor(ChangelogDocument)
                        .Transform(new Point(0, renderedHeight));
                    lastContentBottom = bottom.Y;
                }
                catch (InvalidOperationException)
                {
                    lastContentBottom = contentHeight;
                }
            }
            double fullContentExtent = Math.Max(contentHeight, lastContentBottom + bottomPadding);
            ChangelogDocument.Height = fullContentExtent;
            double distance = Math.Max(0, fullContentExtent - viewportHeight);
            _lastChangelogViewportWidth = FlightDeckChangelogViewport.ActualWidth;
            _lastChangelogViewportHeight = viewportHeight;
            Debug.WriteLine($"CHANGELOG_SCROLL viewport={viewportHeight:0.##} documentActual={ChangelogDocument.ActualHeight:0.##} documentDesired={contentHeight:0.##} lastBottom={lastContentBottom:0.##} padding={bottomPadding:0.##} extent={fullContentExtent:0.##} distance={distance:0.##} speed=8");
            if (distance <= 1) return;

            int scrollMilliseconds = Math.Max(3000, (int)(distance / 8.0 * 1000));
            int initialPause = 3000;
            int bottomPause = 3000;
            DoubleAnimation animation = new(0, -distance, TimeSpan.FromMilliseconds(scrollMilliseconds))
            {
                BeginTime = TimeSpan.FromMilliseconds(initialPause),
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = null
            };
            animation.Completed += async (_, _) =>
            {
                if (!_flightDeckChangelogScrollingEnabled) return;
                try
                {
                    await Task.Delay(bottomPause, _flightDeckCancellation?.Token ?? CancellationToken.None);
                    if (!_flightDeckChangelogScrollingEnabled) return;
                    _changelogTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    _changelogTranslate.Y = 0;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => StartFlightDeckChangelogScroll(false)));
                }
                catch (OperationCanceledException) { }
            };
            _changelogTranslate.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
            _changelogScrollStartCount++;
            Debug.WriteLine($"CHANGELOG_SCROLL_STARTED count={_changelogScrollStartCount} duration={scrollMilliseconds / 1000.0:0.##}s target={-distance:0.##} checkAccess={Dispatcher.CheckAccess()}");
            }
            finally
            {
                _changelogScrollPreparing = false;
            }
        }

        private void FlightDeckChangelogViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_flightDeckChangelogScrollingEnabled || _changelogScrollPreparing
                || FlightDeckHost.Visibility != Visibility.Visible || FlightDeckHost.Opacity <= 0)
                return;
            if (Math.Abs(e.NewSize.Width - _lastChangelogViewportWidth) < 0.5
                && Math.Abs(e.NewSize.Height - _lastChangelogViewportHeight) < 0.5)
                return;
            if (_changelogScrollRestartPending) return;
            _changelogScrollRestartPending = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                _changelogScrollRestartPending = false;
                if (_flightDeckChangelogScrollingEnabled)
                    StartFlightDeckChangelogScroll(false);
            }));
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
            if (FlightDeckRoot is not null)
                FlightDeckRoot.Visibility = Visibility.Collapsed;
            if (FlightDeckFootnoteOverlay is not null)
                FlightDeckFootnoteOverlay.Visibility = Visibility.Collapsed;
            if (PacketIndicator?.RenderTransform is TranslateTransform packet)
            {
                packet.BeginAnimation(TranslateTransform.XProperty, null);
                packet.X = 0;
            }
            if (_changelogTranslate is not null)
            {
                _flightDeckChangelogScrollingEnabled = false;
                _changelogScrollRestartPending = false;
                _changelogScrollPreparing = false;
                _changelogScrollStartCount = 0;
                _lastChangelogViewportWidth = 0;
                _lastChangelogViewportHeight = 0;
                _changelogTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                _changelogTranslate.Y = 0;
                ChangelogDocument.Width = double.NaN;
                ChangelogDocument.Height = double.NaN;
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

        private void AboutSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateFlightDeckFootnoteVisibility();
        }

        private void UpdateFlightDeckFootnoteVisibility()
        {
            if (FlightDeckFootnoteOverlay is null)
                return;

            FlightDeckFootnoteOverlay.Visibility = _flightDeckActive && AboutSurface.ActualWidth >= 900
                ? Visibility.Visible
                : Visibility.Collapsed;
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
