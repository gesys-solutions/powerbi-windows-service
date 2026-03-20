using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PbiBridgeApi.Models;

/// <summary>
/// Request body for POST /v1/migrate.
/// DA-015: source_path + output_path passed to subprocess tableau2pbi — logic stays in Python.
/// </summary>
public sealed class MigrateRequest
{
    [Required]
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("output_path")]
    public string OutputPath { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    public Dictionary<string, object?> Options { get; set; } = new();
}
