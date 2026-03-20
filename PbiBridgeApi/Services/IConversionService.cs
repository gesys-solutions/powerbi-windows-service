namespace PbiBridgeApi.Services;

/// <summary>
/// Runs the tableau2pbi Python subprocess (DA-015: do NOT reimplement conversion logic).
/// </summary>
public interface IConversionService
{
    /// <summary>
    /// Execute tableau2pbi conversion. Returns (stdout, stderr, exitCode).
    /// Timeout: JobTimeoutMinutes (default 10).
    /// </summary>
    Task<(string Stdout, string Stderr, int ExitCode)> RunConversionAsync(
        string sourcePath,
        string outputPath,
        Dictionary<string, object?> options,
        CancellationToken cancellationToken = default);
}
