using RouterPilot.Models;

namespace RouterPilot.Services;

public static class TrafficProcessingProjection
{
    public static TrafficProcessingSnapshot Create(RouterAdvancedSnapshot source) =>
        new(source.SqmEnabled, source.SqmQueueDiscipline, source.SqmDownload, source.SqmUpload,
            source.DpiConfigured, source.DpiRunning,
            SoftwareFlowOffload: null, HardwareFlowOffload: null,
            NetworkAcceleration: null, HardwareAcceleration: null);

    public static string AccelerationSummary(TrafficProcessingSnapshot snapshot) =>
        snapshot.HasKnownAcceleration ? "Acceleration telemetry available" : "Acceleration status could not be determined from the router's available telemetry.";
}
