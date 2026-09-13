namespace RouterPilot.Models;

public enum SqmCapabilityState { Unknown, Native, LegacyReadOnly, Unavailable }

public sealed record SqmConfiguration(bool Enable, int DownloadMbps, int UploadMbps, string Qdisc)
{
    public bool IsValid => DownloadMbps is >= 1 and <= 10000 && UploadMbps is >= 1 and <= 10000 && Qdisc is "cake" or "fq_codel";
}

public sealed record SqmReadResult(SqmCapabilityState Capability, SqmConfiguration? Configuration, string Message);
public sealed record SqmApplyResult(bool Success, bool Verified, string Message);
