using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using RouterPilot.Models;

namespace RouterPilot.Services;

public interface IInternetSpeedTestService
{
    ReadOnlyObservableCollection<SpeedTestResult> History { get; }
    IReadOnlyList<SpeedTestResult> RecentHistory { get; }
    SpeedTestResult Current { get; }
    bool IsRunning { get; }
    string ProgressText { get; }
    string RouterAvailabilityText { get; }
    string? FailureMessage { get; }
    event PropertyChangedEventHandler? PropertyChanged;
    Task InitializeAsync();
    Task<SpeedTestResult> RunAsync(bool routerConnected, bool internetConnected, CancellationToken cancellationToken = default);
    void Cancel();
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// User-initiated Internet throughput tests. Router probing is read-only and
/// conservative: a discovered binary is never executed unless RouterPilot has
/// a verified, safe implementation for that exact backend. The current PC
/// fallback uses Measurement Lab's documented NDT7 discovery and secure WSS
/// protocol; it sends no RouterPilot credentials or router configuration.
/// </summary>
public sealed class InternetSpeedTestService : IInternetSpeedTestService
{
    private const int MaximumHistory = 30;
    private static readonly Uri NdtLocateUri = new("https://locate.measurementlab.net/v2/nearest/ndt/ndt7");
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IRouterManagerProvider _routerManagerProvider;
    private readonly string _historyPath;
    private readonly ObservableCollection<SpeedTestResult> _history = [];
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _runCancellation;
    private bool _initialized;
    private bool _isRunning;
    private string _progressText = "Ready";
    private string _routerAvailabilityText = "Router speed test unavailable. Tests will run from this PC.";
    private string? _failureMessage;
    private SpeedTestResult _current = new() { Status = SpeedTestStatus.Ready };

    public InternetSpeedTestService(IUiDispatcher uiDispatcher, ApplicationDataPathProvider paths,
        IRouterManagerProvider routerManagerProvider)
    {
        _uiDispatcher = uiDispatcher;
        _routerManagerProvider = routerManagerProvider;
        _historyPath = Path.Combine(paths.CurrentPath, "speed-tests.json");
        History = new ReadOnlyObservableCollection<SpeedTestResult>(_history);
    }

    public ReadOnlyObservableCollection<SpeedTestResult> History { get; }
    public IReadOnlyList<SpeedTestResult> RecentHistory => _history.Take(5).ToList();
    public SpeedTestResult Current => _current;
    public bool IsRunning => _isRunning;
    public bool CanRun => !_isRunning;
    public string ProgressText => _progressText;
    public string RouterAvailabilityText => _routerAvailabilityText;
    public string? FailureMessage => _failureMessage;
    public bool HasHistory => _history.Count > 0;
    public string PingDisplay => Current.PingMs is { } ping ? $"{ping:0.#} ms" : "N/A";
    public string DownloadDisplay => Current.DownloadMbps is { } download ? $"{download:0.#} Mbps" : "N/A";
    public string UploadDisplay => Current.UploadMbps is { } upload ? $"{upload:0.#} Mbps" : "N/A";
    public string SourceDisplay => Current.Status == SpeedTestStatus.Completed ? Current.Source == SpeedTestSource.Router ? "Router" : "This PC" : "N/A";
    public string ProviderDisplay => string.IsNullOrWhiteSpace(Current.Provider) ? "N/A" : Current.Provider;
    public string LastTestedDisplay => Current.Status == SpeedTestStatus.Completed ? Current.Timestamp.LocalDateTime.ToString("g") : "N/A";
    public string StatusDisplay => IsRunning ? "Pending" : Current.Status.ToString();
    public string SourceToolTip => Current.Status == SpeedTestStatus.Completed && Current.Source == SpeedTestSource.Router
        ? "Measured from the router. This best represents the router's WAN-side Internet performance."
        : "Measured from this computer. Results may also reflect Wi-Fi or LAN performance between this PC and the router.";
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        List<SpeedTestResult> loaded = [];
        try
        {
            if (File.Exists(_historyPath))
            {
                await using FileStream stream = File.OpenRead(_historyPath);
                SpeedTestStore? store = await JsonSerializer.DeserializeAsync<SpeedTestStore>(stream);
                loaded = store?.Tests ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Optional local history must never interfere with router monitoring.
        }

        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_initialized) return;
            foreach (SpeedTestResult test in loaded
                         .Where(test => test.Status == SpeedTestStatus.Completed)
                         .OrderByDescending(test => test.Timestamp)
                         .Take(MaximumHistory))
                _history.Add(test);
            // Persisted tests belong to Recent Tests. The headline result is a
            // live-session measurement, so a new RouterPilot launch starts Ready.
            _initialized = true;
            Raise(nameof(History));
            Raise(nameof(RecentHistory));
            Raise(nameof(HasHistory));
            RaiseCurrentPresentation();
        });
    }

    public async Task<SpeedTestResult> RunAsync(bool routerConnected, bool internetConnected, CancellationToken cancellationToken = default)
    {
        bool gateAcquired = false;
        CancellationTokenSource? runCancellation = null;
        try
        {
            Debug.WriteLine("RUN-01 entered");
            await _runGate.WaitAsync(cancellationToken);
            gateAcquired = true;

            // Publish visible state before any disk, SSH, HTTP, or WebSocket work.
            // The service is bound directly by Analytics, so every update is made
            // through the application's current WPF dispatcher.
            await SetRunningAsync(true, "Preparing speed test…");
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCancellation.CancelAfter(TimeSpan.FromSeconds(60));
            _runCancellation = runCancellation;

            await InitializeAsync();
            if (!routerConnected || !internetConnected)
            {
                await SetCurrentAsync(new SpeedTestResult
                {
                    Status = SpeedTestStatus.Error,
                    Timestamp = DateTimeOffset.UtcNow,
                    SafeFailureCategory = !routerConnected ? "router-unavailable" : "internet-unavailable"
                }, "Internet speed testing is unavailable.", "Internet connection is unavailable.");
                return _current;
            }

            await SetProgressAsync("Checking router speed-test capability…");
            Debug.WriteLine("RUN-02 router probe starting");
            RouterSpeedTestCapability capability = await DiscoverRouterCapabilityWithTimeoutAsync(runCancellation.Token);
            Debug.WriteLine("RUN-03 router probe finished");
            // No router-native backend or installed binary has been verified for
            // RouterPilot. A detected executable is deliberately not invoked with
            // guessed arguments or a third-party remote endpoint.
            if (!capability.IsSupported)
            {
                await SetRouterAvailabilityAsync(capability.DetectedBinary is null
                    ? "Router speed test unavailable. Tests will run from this PC."
                    : $"Router test tool detected ({capability.DetectedBinary}) but no verified Internet-test backend is available. Tests will run from this PC.");
                await SetProgressAsync("Router speed test unavailable. Testing from this PC…");
                Debug.WriteLine("RUN-04 backend selected: This PC");
                SpeedTestResult result = await RunNdt7PcTestAsync(runCancellation.Token);
                await AddSuccessfulResultAsync(result, runCancellation.Token);
                Debug.WriteLine("RUN-16 history persisted");
                await SetCurrentAsync(result, "Completed");
            }
            else
            {
                await SetCurrentAsync(new SpeedTestResult
                {
                    Status = SpeedTestStatus.Unsupported,
                    Timestamp = DateTimeOffset.UtcNow,
                    SafeFailureCategory = "router-backend-not-implemented"
                }, "Speed test unavailable.", "The available router backend is not supported by RouterPilot.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await SetCurrentAsync(new SpeedTestResult { Status = SpeedTestStatus.Cancelled, Timestamp = DateTimeOffset.UtcNow }, "Cancelled");
        }
        catch (OperationCanceledException)
        {
            await SetCurrentAsync(new SpeedTestResult
            {
                Status = SpeedTestStatus.Error,
                Timestamp = DateTimeOffset.UtcNow,
                SafeFailureCategory = "timed-out"
            }, "Speed test timed out.", "Speed test timed out.");
        }
        catch (SpeedTestFailureException exception)
        {
            await SetCurrentAsync(new SpeedTestResult
            {
                Status = SpeedTestStatus.Error,
                Timestamp = DateTimeOffset.UtcNow,
                SafeFailureCategory = exception.Category
            }, "Speed test failed.", exception.UserMessage);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"SpeedTest failed: {exception.GetType().Name}");
            await SetCurrentAsync(new SpeedTestResult
            {
                Status = SpeedTestStatus.Error,
                Timestamp = DateTimeOffset.UtcNow,
                SafeFailureCategory = ClassifyFailure(exception)
            }, "Speed test failed.", GetSafeFailureMessage(exception));
        }
        finally
        {
            if (ReferenceEquals(_runCancellation, runCancellation))
                _runCancellation = null;
            runCancellation?.Dispose();
            if (gateAcquired)
            {
                await SetRunningAsync(false, _current.Status == SpeedTestStatus.Error ? "Error" : _current.Status.ToString());
                _runGate.Release();
            }
            Debug.WriteLine("RUN-17 returning");
            Debug.WriteLine("RUN-18 finally resetting IsRunning");
        }
        return _current;
    }

    public void Cancel() => _runCancellation?.Cancel();

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        await _uiDispatcher.InvokeAsync(() =>
        {
            _history.Clear();
            Raise(nameof(History));
            Raise(nameof(RecentHistory));
            Raise(nameof(HasHistory));
        });
        await PersistAsync(cancellationToken);
    }

    private async Task<RouterSpeedTestCapability> DiscoverRouterCapabilityAsync(CancellationToken cancellationToken)
    {
        RouterManager router = await _routerManagerProvider.GetRouterManagerAsync(cancellationToken);
        return await router.DiscoverSpeedTestCapabilityAsync(cancellationToken);
    }

    private async Task<RouterSpeedTestCapability> DiscoverRouterCapabilityWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            return await DiscoverRouterCapabilityAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine("SpeedTest router capability probe timed out; using This PC fallback.");
            return new RouterSpeedTestCapability { IsSupported = false };
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"SpeedTest router capability probe failed: {exception.GetType().Name}; using This PC fallback.");
            return new RouterSpeedTestCapability { IsSupported = false };
        }
    }

    private async Task<SpeedTestResult> RunNdt7PcTestAsync(CancellationToken cancellationToken)
    {
        Debug.WriteLine("RUN-05 M-Lab discovery starting");
        (Uri downloadUri, Uri uploadUri) = await DiscoverNdt7ServerAsync(cancellationToken);
        Debug.WriteLine("RUN-06 M-Lab discovery finished");
        var stopwatch = Stopwatch.StartNew();
        await SetProgressAsync("Testing download…");
        double downloadMbps = await RunPhaseAsync("download", () => RunDownloadAsync(downloadUri, cancellationToken), cancellationToken);
        await SetProgressAsync("Testing upload…");
        double uploadMbps = await RunPhaseAsync("upload", () => RunUploadAsync(uploadUri, cancellationToken), cancellationToken);
        stopwatch.Stop();
        var result = new SpeedTestResult
        {
            Timestamp = DateTimeOffset.UtcNow,
            Source = SpeedTestSource.ThisPc,
            // NDT7 does not provide a conventional round-trip ping in this
            // lightweight client, so it is deliberately reported as N/A.
            PingMs = null,
            DownloadMbps = downloadMbps,
            UploadMbps = uploadMbps,
            Duration = stopwatch.Elapsed,
            Provider = "Measurement Lab (M-Lab)",
            ServerDescription = downloadUri.Host,
            Status = SpeedTestStatus.Completed
        };
        Debug.WriteLine("RUN-15 result built");
        return result;
    }

    private async Task<(Uri Download, Uri Upload)> DiscoverNdt7ServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(NdtLocateUri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement results = document.RootElement.GetProperty("results");
            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                throw new JsonException("NDT7 locate service returned no server.");

            JsonElement urls = results[0].GetProperty("urls");
            // The locator provides both ws and wss values. The old implementation
            // selected the first matching value (ws), then rejected it. Select the
            // documented secure URL keys before validating the returned endpoints.
            string? download = FindNdt7Url(urls, "/download");
            string? upload = FindNdt7Url(urls, "/upload");
            if (!Uri.TryCreate(download, UriKind.Absolute, out Uri? downloadUri) ||
                !Uri.TryCreate(upload, UriKind.Absolute, out Uri? uploadUri) ||
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uploadUri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
            {
                throw new JsonException("NDT7 locate service returned no secure server URLs.");
            }
            return (downloadUri, uploadUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or KeyNotFoundException)
        {
            throw new SpeedTestFailureException("server-discovery-failed", "Speed-test server discovery failed.", exception);
        }
    }

    private static string? FindNdt7Url(JsonElement urls, string suffix) =>
        urls.EnumerateObject()
            .Where(item => item.Name.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                           item.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value.GetString())
            .FirstOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                                     string.Equals(uri.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase));

    private static async Task<T> RunPhaseAsync<T>(string phase, Func<Task<T>> run, CancellationToken cancellationToken)
    {
        try
        {
            return await run();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new SpeedTestFailureException($"{phase}-timed-out", $"{char.ToUpperInvariant(phase[0])}{phase[1..]} test timed out.", exception);
        }
        catch (WebSocketException exception)
        {
            throw new SpeedTestFailureException($"{phase}-connection-failed", "Unable to establish a secure connection to the speed-test server.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new SpeedTestFailureException($"{phase}-failed", $"{char.ToUpperInvariant(phase[0])}{phase[1..]} test could not complete.", exception);
        }
    }

    private static async Task<double> RunDownloadAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var socket = CreateNdt7Socket();
        Debug.WriteLine("RUN-07 download connect starting");
        await ConnectWithTimeoutAsync(socket, endpoint, cancellationToken);
        Debug.WriteLine("RUN-08 download connected");
        byte[] buffer = new byte[64 * 1024];
        long received = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            Debug.WriteLine("RUN-09 download running");
            while (socket.State == WebSocketState.Open && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, duration.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Binary) received += result.Count;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && received > 0)
        {
            // The test-duration guard completes a valid transfer.
        }
        finally
        {
            stopwatch.Stop();
            await CloseSocketWithTimeoutAsync(socket, cancellationToken);
        }
        if (received == 0 || stopwatch.Elapsed.TotalSeconds <= 0)
            throw new HttpRequestException("NDT7 download returned no measurement data.");
        Debug.WriteLine("RUN-10 download finished");
        return received * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;
    }

    private static async Task<double> RunUploadAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var socket = CreateNdt7Socket();
        Debug.WriteLine("RUN-11 upload connect starting");
        await ConnectWithTimeoutAsync(socket, endpoint, cancellationToken);
        Debug.WriteLine("RUN-12 upload connected");
        byte[] payload = new byte[64 * 1024];
        long sent = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        duration.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            Debug.WriteLine("RUN-13 upload running");
            while (socket.State == WebSocketState.Open && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, duration.Token);
                sent += payload.Length;
            }
        }
        finally
        {
            stopwatch.Stop();
            await CloseSocketWithTimeoutAsync(socket, cancellationToken);
        }
        if (sent == 0 || stopwatch.Elapsed.TotalSeconds <= 0)
            throw new HttpRequestException("NDT7 upload returned no measurement data.");
        Debug.WriteLine("RUN-14 upload finished");
        return sent * 8d / stopwatch.Elapsed.TotalSeconds / 1_000_000d;
    }

    private static async Task ConnectWithTimeoutAsync(ClientWebSocket socket, Uri endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(endpoint, timeout.Token);
    }

    private static async Task CloseSocketWithTimeoutAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "completed", timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A peer need not acknowledge the close for a completed measurement.
        }
        catch (WebSocketException)
        {
            // Dispose is sufficient once a completed measurement has a result.
        }
    }

    private static ClientWebSocket CreateNdt7Socket()
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("net.measurementlab.ndt.v7");
        return socket;
    }

    private async Task AddSuccessfulResultAsync(SpeedTestResult result, CancellationToken cancellationToken)
    {
        Debug.WriteLine("SVC-16 persistence started");
        await _uiDispatcher.InvokeAsync(() =>
        {
            _history.Insert(0, result);
            while (_history.Count > MaximumHistory) _history.RemoveAt(_history.Count - 1);
        });
        await PersistAsync(cancellationToken);
        await _uiDispatcher.InvokeAsync(() =>
        {
            Raise(nameof(History));
            Raise(nameof(RecentHistory));
            Raise(nameof(HasHistory));
        });
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            List<SpeedTestResult> snapshot = await _uiDispatcher.InvokeAsync(() => _history.ToList());
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            string temporary = _historyPath + ".tmp";
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(new SpeedTestStore { Tests = snapshot }, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporary, _historyPath, true);
        }
        finally { _writeGate.Release(); }
    }

    private Task SetRunningAsync(bool running, string progress) =>
        _uiDispatcher.InvokeAsync(() => SetRunningCore(running, progress));

    private Task SetProgressAsync(string progress) =>
        _uiDispatcher.InvokeAsync(() => SetProgressCore(progress));

    private Task SetCurrentAsync(SpeedTestResult current, string progress, string? failureMessage = null) =>
        _uiDispatcher.InvokeAsync(() => SetCurrentCore(current, progress, failureMessage));

    private Task SetRouterAvailabilityAsync(string availability) =>
        _uiDispatcher.InvokeAsync(() =>
        {
            _routerAvailabilityText = availability;
            Raise(nameof(RouterAvailabilityText));
        });

    private void SetRunningCore(bool running, string progress)
    {
        _isRunning = running;
        _progressText = progress;
        if (running)
        {
            _current = new SpeedTestResult
            {
                Status = SpeedTestStatus.Pending,
                Timestamp = DateTimeOffset.UtcNow
            };
            _failureMessage = null;
            Raise(nameof(Current));
            Raise(nameof(FailureMessage));
            RaiseCurrentPresentation();
        }
        Raise(nameof(IsRunning));
        Raise(nameof(CanRun));
        Raise(nameof(ProgressText));
        Raise(nameof(StatusDisplay));
    }

    private void SetProgressCore(string progress)
    {
        _progressText = progress;
        Raise(nameof(ProgressText));
    }

    private void SetCurrentCore(SpeedTestResult current, string progress, string? failureMessage = null)
    {
        _current = current;
        _progressText = progress;
        _failureMessage = failureMessage;
        Raise(nameof(Current));
        Raise(nameof(FailureMessage));
        RaiseCurrentPresentation();
        Raise(nameof(ProgressText));
    }

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        HttpRequestException => "speed-test-service-unavailable",
        WebSocketException => "speed-test-service-unavailable",
        OperationCanceledException => "timed-out",
        _ => "speed-test-failed"
    };

    private static string GetSafeFailureMessage(Exception exception) => exception switch
    {
        HttpRequestException => "Unable to contact the speed-test service.",
        WebSocketException => "Unable to establish a secure connection to the speed-test server.",
        OperationCanceledException => "Speed test timed out.",
        _ => "RouterPilot could not complete the speed test."
    };

    private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    private void RaiseCurrentPresentation()
    {
        Raise(nameof(PingDisplay));
        Raise(nameof(DownloadDisplay));
        Raise(nameof(UploadDisplay));
        Raise(nameof(SourceDisplay));
        Raise(nameof(ProviderDisplay));
        Raise(nameof(LastTestedDisplay));
        Raise(nameof(StatusDisplay));
        Raise(nameof(SourceToolTip));
    }

    private sealed class SpeedTestStore
    {
        public int FormatVersion { get; init; } = 1;
        public List<SpeedTestResult> Tests { get; init; } = [];
    }

    private sealed class SpeedTestFailureException(string category, string userMessage, Exception innerException)
        : Exception(userMessage, innerException)
    {
        public string Category { get; } = category;
        public string UserMessage { get; } = userMessage;
    }
}
