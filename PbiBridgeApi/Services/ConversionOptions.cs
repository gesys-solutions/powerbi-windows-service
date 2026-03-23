namespace PbiBridgeApi.Services;

public sealed class ValidationOptions
{
    public string WorkspaceRootPath { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\PbiBridge\workspaces"
        : "/tmp/pbi-bridge-workspaces";
    public string DefaultValidator { get; set; } = "contract-check";
    public int JobTimeoutMinutes { get; set; } = 5;
    public bool PowerBiDesktopAvailable { get; set; }
    public bool McpAvailable { get; set; }
    public string[] SupportedFileExtensions { get; set; } = [".pbix", ".pbip", ".zip"];
}
