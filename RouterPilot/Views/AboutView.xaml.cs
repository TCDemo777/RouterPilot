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
                FlightDeckHost.Visibility = Visibility.Visible;
                await CrossfadeToFlightDeckAsync(reducedMotion, cancellationToken);
                StartAmbientPacketAnimation(reducedMotion);
            }
            catch (OperationCanceledException)
            {
                // Navigation away owns cancellation and resets the transient state.
            }
        }

        private void ResetPreflightVisuals()
        {
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

            const int ascentMilliseconds = 2700;
            LaunchTranslation.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(ascentMilliseconds))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
            LaunchTranslation.BeginAnimation(TranslateTransform.YProperty,
                // The logo begins at Canvas.Top 142 and is 104px tall. -380
                // carries its complete rendered bounds beyond the 430px scene
                // viewport with additional clearance, rather than fading it out.
                new DoubleAnimation(0, -380, TimeSpan.FromMilliseconds(ascentMilliseconds))
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
