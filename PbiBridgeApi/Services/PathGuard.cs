namespace PbiBridgeApi.Services;

/// <summary>
/// Filesystem path validation for multi-client isolation (DA-014 / Blocker 1).
/// Ensures client-supplied paths remain within their designated sandbox:
///   {WorkspaceRootPath}/{clientId}/
///
/// Rejects:
///   - UNC paths (\\server\share)
///   - Path traversal attempts (..)
///   - Paths outside the client workspace
///   - Empty / whitespace-only paths
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Validate a single path for a given client against the workspace root.
    /// Returns (true, "") if valid; (false, error message) otherwise.
    /// </summary>
    public static (bool IsValid, string Error) Validate(
        string path,
        string clientId,
        string workspaceRootPath)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Path cannot be empty.");

        if (string.IsNullOrWhiteSpace(clientId))
            return (false, "client_id is required for path validation.");

        if (string.IsNullOrWhiteSpace(workspaceRootPath))
            return (false, "WorkspaceRootPath is not configured.");

        // Reject UNC paths (e.g. \\server\share)
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
            return (false, "UNC paths are not allowed.");

        // Normalize — collapses .. segments and resolves symlinks in the path string
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return (false, $"Invalid path: {ex.Message}");
        }

        // Re-check UNC after normalization (Path.GetFullPath can produce UNC on Windows)
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            return (false, "Resolved path is a UNC path, which is not allowed.");

        // Build the allowed root for this client: {WorkspaceRootPath}/{clientId}
        string allowedRoot;
        try
        {
            allowedRoot = Path.GetFullPath(Path.Combine(workspaceRootPath, clientId));
        }
        catch (Exception ex)
        {
            return (false, $"WorkspaceRootPath configuration error: {ex.Message}");
        }

        // Path must be exactly the allowed root OR be a descendant of it
        var allowedPrefix = allowedRoot + Path.DirectorySeparatorChar;
        bool isContained =
            fullPath.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase);

        if (!isContained)
            return (false, "Path is outside the allowed workspace for this client.");

        return (true, string.Empty);
    }

    /// <summary>
    /// Validate both source_path and output_path for a conversion request.
    /// Returns (true, "") if both are valid; (false, error) on first failure.
    /// </summary>
    public static (bool IsValid, string Error) ValidateConversionPaths(
        string sourcePath,
        string outputPath,
        string clientId,
        string workspaceRootPath)
    {
        var (srcOk, srcErr) = Validate(sourcePath, clientId, workspaceRootPath);
        if (!srcOk)
            return (false, $"source_path rejected: {srcErr}");

        var (outOk, outErr) = Validate(outputPath, clientId, workspaceRootPath);
        if (!outOk)
            return (false, $"output_path rejected: {outErr}");

        return (true, string.Empty);
    }
}
