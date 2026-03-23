namespace PbiBridgeApi.Services;

public interface IValidationService
{
    Task<ValidationExecutionResult> RunValidationAsync(
        string artifactPath,
        string validator,
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default);
}

public sealed class ValidationExecutionResult
{
    public ValidationStatus Status { get; init; } = ValidationStatus.Succeeded;
    public string Validator { get; init; } = "contract-check";
    public string Summary { get; init; } = string.Empty;
    public string? Error { get; init; }
    public List<ValidationCheckRecord> Checks { get; init; } = new();
}
