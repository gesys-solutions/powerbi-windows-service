namespace PbiBridgeApi.Services;

public sealed class ValidationJobRecord
{
    public string JobId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ArtifactPath { get; init; } = string.Empty;
    public string Validator { get; set; } = "contract-check";
    public ValidationStatus Status { get; set; } = ValidationStatus.Queued;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public ValidationReportDocument? Report { get; set; }
}

public sealed class ValidationReportDocument
{
    public string Summary { get; set; } = string.Empty;
    public string? Error { get; set; }
    public bool FallbackNonBlocking { get; set; } = true;
    public string ConversionStatusImpact { get; set; } = "none";
    public List<ValidationCheckRecord> Checks { get; set; } = new();
}

public sealed class ValidationCheckRecord
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public enum ValidationStatus
{
    NotRequested,
    Queued,
    Running,
    Succeeded,
    Failed,
    Unavailable,
    Skipped,
}

public static class ValidationStatusExtensions
{
    public static string ToApiValue(this ValidationStatus status)
        => status switch
        {
            ValidationStatus.NotRequested => "not_requested",
            ValidationStatus.Queued => "queued",
            ValidationStatus.Running => "running",
            ValidationStatus.Succeeded => "succeeded",
            ValidationStatus.Failed => "failed",
            ValidationStatus.Unavailable => "unavailable",
            ValidationStatus.Skipped => "skipped",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };

    public static bool IsTerminal(this ValidationStatus status)
        => status is ValidationStatus.Succeeded or ValidationStatus.Failed or ValidationStatus.Unavailable or ValidationStatus.Skipped;
}
