namespace PbiBridgeApi.Services;

/// <summary>
/// Represents a single conversion job.
/// DA-014: client_id enforced — a client cannot access another client's job.
/// </summary>
public sealed class JobRecord
{
    public string JobId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
