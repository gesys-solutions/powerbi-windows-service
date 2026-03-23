using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PbiBridgeApi.Models;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Controllers;

/// <summary>
/// Validation-only contract for the optional Windows validator.
/// </summary>
[ApiController]
[Route("v1")]
public class ValidationController : ControllerBase
{
    private const string ClientIdKey = "client_id";
    private const string AdminClientId = "__admin__";

    private readonly IValidationJobManager _jobManager;
    private readonly IValidationService _validationService;
    private readonly ValidationOptions _validationOptions;
    private readonly ILogger<ValidationController> _logger;

    public ValidationController(
        IValidationJobManager jobManager,
        IValidationService validationService,
        IOptions<ValidationOptions> validationOptions,
        ILogger<ValidationController> logger)
    {
        _jobManager = jobManager;
        _validationService = validationService;
        _validationOptions = validationOptions.Value;
        _logger = logger;
    }

    [HttpPost("validate")]
    [ProducesResponseType(typeof(ValidateResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Validate([FromBody] ValidateRequest request)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        if (IsAdminClient(clientId))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "X-Admin-Key is read-only on /v1/* diagnostics. Use X-API-Key to queue a validation job." });

        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(request.ArtifactPath))
            return BadRequest(new { error = "artifact_path is required" });

        var validator = string.IsNullOrWhiteSpace(request.Validator)
            ? _validationOptions.DefaultValidator
            : request.Validator.Trim();

        var (valid, pathError) = PathGuard.Validate(
            request.ArtifactPath,
            clientId,
            _validationOptions.WorkspaceRootPath);

        if (!valid)
        {
            _logger.LogWarning("[{ClientId}] Validation request rejected: {Error}", clientId, pathError);
            return BadRequest(new { error = $"artifact_path rejected: {pathError}" });
        }

        var jobId = _jobManager.CreateJob(clientId, request.ArtifactPath, validator);

        _ = RunValidationBackgroundAsync(jobId, clientId, request, validator);

        return Accepted(new ValidateResponse
        {
            JobId = jobId,
            ValidationStatus = ValidationStatus.Queued.ToApiValue(),
            Message = "Validation job queued.",
        });
    }

    [HttpGet("validation-status/{jobId}")]
    [ProducesResponseType(typeof(ValidationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetValidationStatus(string jobId)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId, allowAdminOverride: IsAdminClient(clientId));
        if (job is null)
            return NotFound(new { error = $"Validation job '{jobId}' not found." });

        return Ok(new ValidationStatusResponse
        {
            JobId = job.JobId,
            ValidationStatus = job.Status.ToApiValue(),
            Validator = job.Validator,
            CreatedAt = job.CreatedAt,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Error = job.Error,
        });
    }

    [HttpGet("validation-report/{jobId}")]
    [ProducesResponseType(typeof(ValidationReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult GetValidationReport(string jobId)
    {
        var clientId = GetClientId();
        if (clientId is null)
            return Unauthorized(new { error = "client_id missing — auth middleware failed" });

        var job = _jobManager.GetJob(jobId, clientId, allowAdminOverride: IsAdminClient(clientId));
        if (job is null)
            return NotFound(new { error = $"Validation job '{jobId}' not found." });

        if (!job.Status.IsTerminal())
            return Conflict(new { error = $"Validation job '{jobId}' is not completed yet (status: {job.Status.ToApiValue()})." });

        var report = job.Report ?? new ValidationReportDocument
        {
            Summary = job.Error ?? "Validation completed without a structured report.",
            Error = job.Error,
            FallbackNonBlocking = true,
            ConversionStatusImpact = "none",
            Checks = new List<ValidationCheckRecord>(),
        };

        return Ok(new ValidationReportResponse
        {
            JobId = job.JobId,
            ValidationStatus = job.Status.ToApiValue(),
            Validator = job.Validator,
            ArtifactPath = job.ArtifactPath,
            Summary = report.Summary,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            Error = report.Error ?? job.Error,
            FallbackNonBlocking = report.FallbackNonBlocking,
            ConversionStatusImpact = report.ConversionStatusImpact,
            Checks = report.Checks
                .Select(check => new ValidationCheckResponse
                {
                    Name = check.Name,
                    Status = check.Status,
                    Detail = check.Detail,
                })
                .ToList(),
        });
    }

    private string? GetClientId() =>
        HttpContext.Items.TryGetValue(ClientIdKey, out var value) ? value as string : null;

    private static bool IsAdminClient(string clientId)
        => string.Equals(clientId, AdminClientId, StringComparison.Ordinal);

    private async Task RunValidationBackgroundAsync(
        string jobId,
        string clientId,
        ValidateRequest request,
        string validator)
    {
        try
        {
            _jobManager.UpdateJob(jobId, clientId, ValidationStatus.Running);

            var result = await _validationService.RunValidationAsync(
                request.ArtifactPath,
                validator,
                request.Options,
                CancellationToken.None);

            var report = new ValidationReportDocument
            {
                Summary = result.Summary,
                Error = result.Error,
                FallbackNonBlocking = true,
                ConversionStatusImpact = "none",
                Checks = result.Checks
                    .Select(check => new ValidationCheckRecord
                    {
                        Name = check.Name,
                        Status = check.Status,
                        Detail = check.Detail,
                    })
                    .ToList(),
            };

            _jobManager.UpdateJob(jobId, clientId, result.Status, result.Error, report);
            _logger.LogInformation("[{ClientId}] Validation job {JobId} finished with status {Status}", clientId, jobId, result.Status.ToApiValue());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ClientId}] Unexpected error running validation job {JobId}", clientId, jobId);
            _jobManager.UpdateJob(
                jobId,
                clientId,
                ValidationStatus.Unavailable,
                ex.Message,
                new ValidationReportDocument
                {
                    Summary = "Validator unavailable — conversion result must remain unchanged.",
                    Error = ex.Message,
                    FallbackNonBlocking = true,
                    ConversionStatusImpact = "none",
                    Checks = new List<ValidationCheckRecord>
                    {
                        new()
                        {
                            Name = "validator_runtime",
                            Status = "unavailable",
                            Detail = ex.Message,
                        },
                    },
                });
        }
    }
}
