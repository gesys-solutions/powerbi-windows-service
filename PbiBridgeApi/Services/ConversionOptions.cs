namespace PbiBridgeApi.Services;

/// <summary>
/// Configuration options for the Conversion subsystem.
/// Bound from appsettings.json section "Conversion".
/// </summary>
public sealed class ConversionOptions
{
    /// <summary>Python executable path.</summary>
    public string PythonPath { get; set; } = "python";

    /// <summary>Optional path to tableau2pbi CLI script. Empty = use -m module.</summary>
    public string Tableau2PbiPath { get; set; } = string.Empty;

    /// <summary>Max time to wait for a subprocess to complete (minutes).</summary>
    public int JobTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Root directory under which per-client sandboxes live.
    /// Each client is isolated to: {WorkspaceRootPath}/{clientId}/
    /// DA-014: paths outside this sandbox are rejected with 400.
    /// </summary>
    public string WorkspaceRootPath { get; set; } = @"C:\PbiBridge\workspaces";
}
