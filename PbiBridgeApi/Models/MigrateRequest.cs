using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

public sealed class ValidateRequest
{
    [Required]
    [JsonPropertyName("artifact_path")]
    public string ArtifactPath { get; set; } = string.Empty;

    [JsonPropertyName("validator")]
    public string Validator { get; set; } = "contract-check";

    [JsonPropertyName("options")]
    public Dictionary<string, object?> Options { get; set; } = new();
}
