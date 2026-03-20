using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

/// <summary>Response for POST /v1/migrate (202 Accepted).</summary>
public sealed class MigrateResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "Conversion job queued.";
}
