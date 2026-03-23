using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

public sealed class ValidateResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("validation_status")]
    public string ValidationStatus { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = "Validation job queued.";
}
