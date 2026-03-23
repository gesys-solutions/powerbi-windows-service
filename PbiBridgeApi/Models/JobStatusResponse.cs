using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

public sealed class ValidationStatusResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("validation_status")]
    public string ValidationStatus { get; set; } = string.Empty;

    [JsonPropertyName("validator")]
    public string Validator { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
