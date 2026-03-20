using Microsoft.AspNetCore.Mvc;
using PbiBridgeApi.Models;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Controllers;

/// <summary>
/// S2.4 ConversionController — POST /v1/migrate + GET /v1/status/{jobId} + GET /v1/result/{jobId}.
/// DA-013: X-API-Key required (enforced by ApiKeyMiddleware — no attribute needed).
/// DA-014: strict client_id isolation — jobs are invisible across clients.
/// DA-015: conversion logic stays in Python subprocess (ConversionService).
/// </summary>
[ApiController]
[Route("v1")]
public class ConversionController : ControllerBase
{
    private const string ClientIdKey = "client_id";

    private readonly IJobManager _jobManager;
    private readonly IConversionService _conversionService;
    private readonly ILogger<ConversionController> _logger;

    public ConversionController(
        IJobManager jobManager,
        IConversionService conversionService,
        ILogger<ConversionController> logger)
    {
        _jobManager = jobManager;
        _conversionService = conversionService;
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // POST /v1/migrate
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queue a new Tableau → Power BI conversion job.
    /// Returns 202 Accepted with the job_id.
    /// DA-015: subprocess tableau2pbi called asynchronously in background.
    /// </summary>
    [HttpPost("migrate")]
    [ProducesResponseType(typeof(MigrateResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Migrate([FromBody] MigrateRequest request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var jobId = _jobManager.CreateJob(clientId);

        _logger.LogInformation("[{ClientId}] Queuing migration job {JobId}: {Src} -> {Dst}",
            clientId, jobId, request.SourcePath, request.OutputPath);

        // Fire-and-forget: run subprocess in background, update job status on completion
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

        // DA-014: GetJob returns null if job exists but belongs to another client
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
            Stdout = null, // stdout captured in Result field as output_path
            CompletedAt = job.CompletedAt,
        });
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private string? GetClientId() =>
        HttpContext.Items.TryGetValue(ClientIdKey, out var v) ? v as string : null;

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
                var errorMsg = string.IsNullOrWhiteSpace(stderr) ? $"Exit code {exitCode}" : stderr.Trim();
                _jobManager.UpdateJob(
                    jobId, clientId, Services.JobStatus.Failed,
                    error: errorMsg);
                _logger.LogWarning("[{ClientId}] Job {JobId} failed (exit {Code}): {Err}",
                    clientId, jobId, exitCode, errorMsg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ClientId}] Unexpected error running job {JobId}", clientId, jobId);
            _jobManager.UpdateJob(jobId, clientId, Services.JobStatus.Failed, error: ex.Message);
        }
    }
}
