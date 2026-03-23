using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbiBridgeApi.Controllers;
using PbiBridgeApi.Models;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Tests;

public sealed class MockValidationService : IValidationService
{
    public ValidationExecutionResult Result { get; set; } = new()
    {
        Status = ValidationStatus.Succeeded,
        Validator = "contract-check",
        Summary = "Validation succeeded.",
        Checks = new List<ValidationCheckRecord>
        {
            new()
            {
                Name = "artifact_exists",
                Status = "passed",
                Detail = "Artifact exists.",
            },
        },
    };

    public Task<ValidationExecutionResult> RunValidationAsync(
        string artifactPath,
        string validator,
        IReadOnlyDictionary<string, object?> options,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ValidationExecutionResult
        {
            Status = Result.Status,
            Validator = Result.Validator,
            Summary = Result.Summary,
            Error = Result.Error,
            Checks = Result.Checks
                .Select(check => new ValidationCheckRecord
                {
                    Name = check.Name,
                    Status = check.Status,
                    Detail = check.Detail,
                })
                .ToList(),
        });
}

internal static class ValidationControllerFactory
{
    public static readonly string TestWorkspaceRoot = Path.Combine(Path.GetTempPath(), "pbi-validator-test-workspaces");

    public static ValidationController Create(
        IValidationJobManager jobManager,
        IValidationService validationService,
        string clientId = "acme",
        string? workspaceRoot = null)
    {
        var options = Options.Create(new ValidationOptions
        {
            WorkspaceRootPath = workspaceRoot ?? TestWorkspaceRoot,
            DefaultValidator = "contract-check",
            SupportedFileExtensions = [".pbix", ".pbip", ".zip"],
        });

        var controller = new ValidationController(
            jobManager,
            validationService,
            options,
            NullLogger<ValidationController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.Items["client_id"] = clientId;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    public static ValidationController CreateUnauthenticated(
        IValidationJobManager jobManager,
        IValidationService validationService)
    {
        var options = Options.Create(new ValidationOptions
        {
            WorkspaceRootPath = TestWorkspaceRoot,
            DefaultValidator = "contract-check",
            SupportedFileExtensions = [".pbix", ".pbip", ".zip"],
        });

        var controller = new ValidationController(
            jobManager,
            validationService,
            options,
            NullLogger<ValidationController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { Response = { Body = new MemoryStream() } },
        };

        return controller;
    }

    public static string ValidArtifactPath(string clientId = "acme", string file = "report.pbix")
        => Path.Combine(TestWorkspaceRoot, clientId, file);
}

public class ValidateEndpointTests : IDisposable
{
    private readonly ValidationJobManager _jobManager = new();
    private readonly MockValidationService _validationService = new();

    public ValidateEndpointTests()
    {
        Directory.CreateDirectory(Path.Combine(ValidationControllerFactory.TestWorkspaceRoot, "acme"));
    }

    [Fact]
    public void Validate_ValidRequest_Returns202WithQueuedJob()
    {
        var artifactPath = ValidationControllerFactory.ValidArtifactPath();
        File.WriteAllText(artifactPath, "pbix");

        var controller = ValidationControllerFactory.Create(_jobManager, _validationService);
        var request = new ValidateRequest { ArtifactPath = artifactPath };

        var result = controller.Validate(request) as AcceptedResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);

        var response = result.Value as ValidateResponse;
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.JobId));
        Assert.Equal("queued", response.ValidationStatus);
    }

    [Fact]
    public void Validate_NoClientId_Returns401()
    {
        var controller = ValidationControllerFactory.CreateUnauthenticated(_jobManager, _validationService);
        var request = new ValidateRequest { ArtifactPath = ValidationControllerFactory.ValidArtifactPath() };

        var result = controller.Validate(request) as UnauthorizedObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public void Validate_MissingArtifactPath_Returns400()
    {
        var controller = ValidationControllerFactory.Create(_jobManager, _validationService);
        var request = new ValidateRequest { ArtifactPath = "" };

        var result = controller.Validate(request) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public void Validate_PathTraversal_Returns400()
    {
        var controller = ValidationControllerFactory.Create(_jobManager, _validationService);
        var request = new ValidateRequest
        {
            ArtifactPath = Path.Combine(ValidationControllerFactory.TestWorkspaceRoot, "acme", "..", "evil", "report.pbix"),
        };

        var result = controller.Validate(request) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(ValidationControllerFactory.TestWorkspaceRoot))
            Directory.Delete(ValidationControllerFactory.TestWorkspaceRoot, recursive: true);
    }
}

public class GetValidationStatusEndpointTests
{
    private readonly ValidationJobManager _jobManager = new();
    private readonly MockValidationService _validationService = new();

    [Fact]
    public void GetValidationStatus_ValidJobId_ReturnsStatus()
    {
        var jobId = _jobManager.CreateJob("acme", "artifact.pbix", "contract-check");
        var controller = ValidationControllerFactory.Create(_jobManager, _validationService, clientId: "acme");

        var result = controller.GetValidationStatus(jobId) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as ValidationStatusResponse;
        Assert.NotNull(response);
        Assert.Equal("queued", response.ValidationStatus);
        Assert.Equal("contract-check", response.Validator);
    }

    [Fact]
    public void GetValidationStatus_OtherClientJob_Returns404()
    {
        var jobId = _jobManager.CreateJob("acme", "artifact.pbix", "contract-check");
        var controller = ValidationControllerFactory.Create(_jobManager, _validationService, clientId: "other-client");

        var result = controller.GetValidationStatus(jobId) as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }
}

public class GetValidationReportEndpointTests
{
    private readonly ValidationJobManager _jobManager = new();
    private readonly MockValidationService _validationService = new();

    [Fact]
    public void GetValidationReport_TerminalJob_ReturnsStructuredReport()
    {
        var jobId = _jobManager.CreateJob("acme", "artifact.pbix", "contract-check");
        _jobManager.UpdateJob(
            jobId,
            "acme",
            ValidationStatus.Unavailable,
            "Desktop validator offline.",
            new ValidationReportDocument
            {
                Summary = "Validator unavailable — conversion result must remain unchanged.",
                Error = "Desktop validator offline.",
                FallbackNonBlocking = true,
                ConversionStatusImpact = "none",
                Checks = new List<ValidationCheckRecord>
                {
                    new()
                    {
                        Name = "validator_backend",
                        Status = "unavailable",
                        Detail = "Desktop validator offline.",
                    },
                },
            });

        var controller = ValidationControllerFactory.Create(_jobManager, _validationService, clientId: "acme");
        var result = controller.GetValidationReport(jobId) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as ValidationReportResponse;
        Assert.NotNull(response);
        Assert.Equal("unavailable", response.ValidationStatus);
        Assert.True(response.FallbackNonBlocking);
        Assert.Equal("none", response.ConversionStatusImpact);
        Assert.Single(response.Checks);
    }

    [Fact]
    public void GetValidationReport_QueuedJob_Returns409()
    {
        var jobId = _jobManager.CreateJob("acme", "artifact.pbix", "contract-check");
        var controller = ValidationControllerFactory.Create(_jobManager, _validationService, clientId: "acme");

        var result = controller.GetValidationReport(jobId) as ConflictObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }
}
