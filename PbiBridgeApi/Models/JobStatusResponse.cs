using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

/// <summary>Response for GET /v1/status/{jobId}.</summary>
public sealed class JobStatusResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
