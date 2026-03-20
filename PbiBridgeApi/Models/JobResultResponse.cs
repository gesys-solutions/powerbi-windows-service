using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

/// <summary>Response for GET /v1/result/{jobId} — only when status=completed.</summary>
public sealed class JobResultResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("output_path")]
    public string? OutputPath { get; set; }

    [JsonPropertyName("stdout")]
    public string? Stdout { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }
}
