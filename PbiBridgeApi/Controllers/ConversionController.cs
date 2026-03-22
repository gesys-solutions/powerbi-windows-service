using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PbiBridgeApi.Models;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Controllers;

/// <summary>
/// S2.4 ConversionController — POST /v1/migrate + GET /v1/status/{jobId} + GET /v1/result/{jobId}.
/// DA-013: X-API-Key required (enforced by ApiKeyMiddleware — no attribute needed).
/// DA-014: strict client_id isolation — jobs and filesystem paths are sandboxed per client.
/// DA-015: conversion logic stays in Python subprocess (ConversionService).
/// </summary>
[ApiController]
[Route("v1")]
public class ConversionController : ControllerBase
{
    private const string ClientIdKey = "client_id";
    private static readonly Regex UnsafeFileNameChars = new("[^A-Za-z0-9._-]", RegexOptions.Compiled);

    private readonly IJobManager _jobManager;
    private readonly IConversionService _conversionService;
    private readonly ILogger<ConversionController> _logger;
    private readonly ConversionOptions _conversionOptions;

    public ConversionController(
        IJobManager jobManager,
        IConversionService conversionService,
        ILogger<ConversionController> logger,
        IOptions<ConversionOptions> conversionOptions)
    {
        _jobManager = jobManager;
        _conversionService = conversionService;
        _logger = logger;
        _conversionOptions = conversionOptions.Value;
    }

    // -----------------------------------------------------------------------
    // POST /v1/migrate
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queue a new Tableau → Power BI conversion job.
    /// Supports either:
    /// - JSON body with source_path/output_path
    /// - multipart/form-data with a file upload (the file is stored in the client's sandbox)
    ///
    /// Returns 202 Accepted with the job_id.
    /// DA-014: source_path and output_path are validated to be inside {WorkspaceRootPath}/{clientId}/.
    /// DA-015: subprocess tableau2pbi called asynchronously in background.
    /// </summary>
    [HttpPost("migrate")]
    [Consumes("application/json", "multipart/form-data")]
    [ProducesResponseType(typeof(MigrateResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Migrate()
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var jobId = _jobManager.CreateJob(clientId);
        var request = await BuildMigrateRequestAsync(clientId, jobId);
        if (request is null)
            return BadRequest(new { error = "Request body invalid. Provide JSON source_path/output_path or multipart form-data with a file field." });

        var (pathsValid, pathError) = PathGuard.ValidateConversionPaths(
            request.SourcePath,
            request.OutputPath,
            clientId,
            _conversionOptions.WorkspaceRootPath);

        if (!pathsValid)
        {
            _logger.LogWarning("[{ClientId}] Path validation rejected request: {Error}", clientId, pathError);
            return BadRequest(new { error = pathError });
        }

        _logger.LogDebug("[{ClientId}] Queuing migration job {JobId}", clientId, jobId);
        _logger.LogInformation("[{ClientId}] Conversion job {JobId} queued.", clientId, jobId);

        _ = RunConversionBackgroundAsync(jobId, clientId, request);

        return Accepted(new MigrateResponse
        {
            JobId = jobId,
            Status = "pending",
            Message = "Conversion job queued.",
        });
    }

    // -----------------------------------------------------------------------
    // GET /v1/status/{jobId}
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get the status of a conversion job.
    /// DA-014: returns 404 if job_id exists but belongs to a different client.
    /// </summary>
    [HttpGet("status/{jobId}")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetStatus(string jobId)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId);
        if (job is null)
            return NotFound(new { error = $"Job '{jobId}' not found." });

        return Ok(new JobStatusResponse
        {
            JobId = job.JobId,
            Status = job.Status.ToString().ToLowerInvariant(),
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Error = job.Error,
        });
    }

    // -----------------------------------------------------------------------
    // GET /v1/result/{jobId}
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get the result of a completed conversion job.
    /// Returns 404 if not found (DA-014), 409 if not yet completed.
    /// </summary>
    [HttpGet("result/{jobId}")]
    [ProducesResponseType(typeof(JobResultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult GetResult(string jobId)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId);
        if (job is null)
            return NotFound(new { error = $"Job '{jobId}' not found." });

        if (job.Status != Services.JobStatus.Completed && job.Status != Services.JobStatus.Failed)
            return Conflict(new { error = $"Job '{jobId}' is not completed yet (status: {job.Status})." });

        return Ok(new JobResultResponse
        {
            JobId = job.JobId,
            Status = job.Status.ToString().ToLowerInvariant(),
            OutputPath = job.Result,
            Stdout = null,
            CompletedAt = job.CompletedAt,
        });
    }

    // -----------------------------------------------------------------------
    // GET /v1/artifacts/{jobId}
    // -----------------------------------------------------------------------

    [HttpGet("artifacts/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult GetArtifacts(string jobId)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId);
        if (job is null)
            return NotFound(new { error = $"Job '{jobId}' not found." });

        if (job.Status != Services.JobStatus.Completed)
            return Conflict(new { error = $"Job '{jobId}' is not completed yet (status: {job.Status})." });

        if (string.IsNullOrWhiteSpace(job.Result))
            return Ok(new { job_id = job.JobId, artifacts = Array.Empty<object>(), zip_url = (string?)null });

        var outputRoot = Path.GetFullPath(job.Result);
        var (valid, pathError) = PathGuard.Validate(outputRoot, clientId, _conversionOptions.WorkspaceRootPath);
        if (!valid)
            return BadRequest(new { error = $"artifact root rejected: {pathError}" });

        if (!Directory.Exists(outputRoot))
            return Ok(new { job_id = job.JobId, artifacts = Array.Empty<object>(), zip_url = (string?)null });

        var files = Directory
            .EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? zipUrl = null;
        var artifacts = files.Select(info =>
        {
            var relativePath = Path.GetRelativePath(outputRoot, info.FullName).Replace('\\', '/');
            var downloadUrl = $"/v1/artifacts/{job.JobId}/download?path={Uri.EscapeDataString(relativePath)}";
            if (zipUrl is null && string.Equals(info.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
                zipUrl = downloadUrl;

            return new
            {
                name = relativePath,
                type = GuessArtifactType(info.Extension),
                size_bytes = info.Length,
                download_url = downloadUrl,
                created_at = info.CreationTimeUtc.ToString("O"),
            };
        }).ToList();

        return Ok(new
        {
            job_id = job.JobId,
            artifacts,
            zip_url = zipUrl,
        });
    }

    // -----------------------------------------------------------------------
    // GET /v1/artifacts/{jobId}/download
    // -----------------------------------------------------------------------

    [HttpGet("artifacts/{jobId}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult DownloadArtifact(string jobId, [FromQuery] string? path = null)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId);
        if (job is null)
            return NotFound(new { error = $"Job '{jobId}' not found." });

        if (job.Status != Services.JobStatus.Completed)
            return Conflict(new { error = $"Job '{jobId}' is not completed yet (status: {job.Status})." });

        if (string.IsNullOrWhiteSpace(job.Result) || !Directory.Exists(job.Result))
            return NotFound(new { error = $"Artifacts for job '{jobId}' are not available." });

        var outputRoot = Path.GetFullPath(job.Result);
        var (valid, pathError) = PathGuard.Validate(outputRoot, clientId, _conversionOptions.WorkspaceRootPath);
        if (!valid)
            return BadRequest(new { error = $"artifact root rejected: {pathError}" });

        string targetPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var zipCandidate = Directory
                .EnumerateFiles(outputRoot, "*.zip", SearchOption.AllDirectories)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (zipCandidate is null)
                return BadRequest(new { error = "Query parameter 'path' is required when no ZIP artifact is available." });

            targetPath = zipCandidate;
        }
        else
        {
            var relativePath = path.Replace('/', Path.DirectorySeparatorChar);
            targetPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));

            var rootPrefix = outputRoot.EndsWith(Path.DirectorySeparatorChar)
                ? outputRoot
                : outputRoot + Path.DirectorySeparatorChar;

            var contained = targetPath.Equals(outputRoot, StringComparison.OrdinalIgnoreCase)
                || targetPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);

            if (!contained)
                return BadRequest(new { error = "Artifact path is outside the allowed output root." });
        }

        if (!System.IO.File.Exists(targetPath))
            return NotFound(new { error = $"Artifact '{path ?? Path.GetFileName(targetPath)}' not found." });

        return PhysicalFile(
            targetPath,
            "application/octet-stream",
            fileDownloadName: Path.GetFileName(targetPath),
            enableRangeProcessing: true);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private string? GetClientId() =>
        HttpContext.Items.TryGetValue(ClientIdKey, out var v) ? v as string : null;

    private async Task<MigrateRequest?> BuildMigrateRequestAsync(string clientId, string jobId)
    {
        if (Request.HasFormContentType)
            return await BuildMigrateRequestFromFormAsync(clientId, jobId);

        return await Request.ReadFromJsonAsync<MigrateRequest>();
    }

    private async Task<MigrateRequest?> BuildMigrateRequestFromFormAsync(string clientId, string jobId)
    {
        var form = await Request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length <= 0)
            return null;

        var clientRoot = Path.Combine(_conversionOptions.WorkspaceRootPath, clientId);
        var uploadsRoot = Path.Combine(clientRoot, "uploads");
        var outputRoot = Path.Combine(clientRoot, "outputs", jobId);
        Directory.CreateDirectory(uploadsRoot);
        Directory.CreateDirectory(outputRoot);

        var safeName = SanitizeFileName(file.FileName);
        var sourcePath = Path.Combine(uploadsRoot, $"{jobId}-{safeName}");

        await using (var stream = System.IO.File.Create(sourcePath))
        {
            await file.CopyToAsync(stream);
        }

        return new MigrateRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputRoot,
            Options = new Dictionary<string, object?>(),
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        var trimmed = Path.GetFileName(fileName).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "upload.twbx";

        return UnsafeFileNameChars.Replace(trimmed, "_");
    }

    private static string GuessArtifactType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".pbix" => "pbix",
            ".json" => "mapping",
            ".log" => "log",
            ".md" or ".txt" or ".html" => "report",
            _ => "other",
        };
    }

    /// <summary>
    /// Background task: call ConversionService subprocess, update job status.
    /// DA-015: subprocess call only — never reimplementing tableau2pbi logic.
    /// </summary>
    private async Task RunConversionBackgroundAsync(
        string jobId,
        string clientId,
        MigrateRequest request)
    {
        try
        {
            _jobManager.UpdateJob(jobId, clientId, Services.JobStatus.Running);

            var (stdout, stderr, exitCode) = await _conversionService.RunConversionAsync(
                request.SourcePath,
                request.OutputPath,
                request.Options);

            if (exitCode == 0)
            {
                _jobManager.UpdateJob(
                    jobId, clientId, Services.JobStatus.Completed,
                    result: request.OutputPath);
                _logger.LogInformation("[{ClientId}] Job {JobId} completed successfully.", clientId, jobId);
            }
            else
            {
                var stderrSummary = string.IsNullOrWhiteSpace(stderr)
                    ? $"Exit code {exitCode}"
                    : $"Exit code {exitCode}: {stderr.Trim()[..Math.Min(stderr.Trim().Length, 200)]}";
                var errorMsg = string.IsNullOrWhiteSpace(stderr) ? $"Exit code {exitCode}" : stderr.Trim();
                _jobManager.UpdateJob(
                    jobId, clientId, Services.JobStatus.Failed,
                    error: errorMsg);
                _logger.LogWarning("[{ClientId}] Job {JobId} failed: {Summary}", clientId, jobId, stderrSummary);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ClientId}] Unexpected error running job {JobId}", clientId, jobId);
            _jobManager.UpdateJob(jobId, clientId, Services.JobStatus.Failed, error: ex.Message);
        }
    }
}
