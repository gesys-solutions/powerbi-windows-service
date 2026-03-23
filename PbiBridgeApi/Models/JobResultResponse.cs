using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

public sealed class ValidationReportResponse
{
    [JsonPropertyName("job_id")]
    public string JobId { get; set; } = string.Empty;

    [JsonPropertyName("validation_status")]
    public string ValidationStatus { get; set; } = string.Empty;

    [JsonPropertyName("validator")]
    public string Validator { get; set; } = string.Empty;

    [JsonPropertyName("artifact_path")]
    public string ArtifactPath { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; set; }

    [JsonPropertyName("completed_at")]
    public DateTime? CompletedAt { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("fallback_non_blocking")]
    public bool FallbackNonBlocking { get; set; } = true;

    [JsonPropertyName("conversion_status_impact")]
    public string ConversionStatusImpact { get; set; } = "none";

    [JsonPropertyName("checks")]
    public List<ValidationCheckResponse> Checks { get; set; } = new();
}

public sealed class ValidationCheckResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}
