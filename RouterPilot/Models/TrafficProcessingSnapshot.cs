namespace RouterPilot.Models;

public sealed record TrafficProcessingSnapshot(
    bool? SqmEnabled,
    string SqmQueueDiscipline,
    string SqmDownload,
    string SqmUpload,
    bool? DpiConfigured,
    bool? DpiRunning,
    bool? SoftwareFlowOffload,
    bool? HardwareFlowOffload,
    bool? NetworkAcceleration,
    bool? HardwareAcceleration)
{
    public bool HasKnownAcceleration => SoftwareFlowOffload.HasValue || HardwareFlowOffload.HasValue || NetworkAcceleration.HasValue || HardwareAcceleration.HasValue;
}
