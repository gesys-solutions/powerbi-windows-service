using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using PbiBridgeApi.Controllers;
using PbiBridgeApi.Models;
using PbiBridgeApi.Services;
using Xunit;

namespace PbiBridgeApi.Tests;

// ── Mock ConversionService ────────────────────────────────────────────────────

/// <summary>
/// Deterministic mock — no subprocess called (DA-015: subprocess only in production).
/// Returns success unless sourcePath contains "FAIL".
/// </summary>
public sealed class MockConversionService : IConversionService
{
    public int CallCount { get; private set; }
    public string? LastSourcePath { get; private set; }

    public Task<(string Stdout, string Stderr, int ExitCode)> RunConversionAsync(
        string sourcePath,
        string outputPath,
        Dictionary<string, object?> options,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSourcePath = sourcePath;

        if (sourcePath.Contains("FAIL", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(("", "Simulated error", 1));

        return Task.FromResult(($"Converted {sourcePath} -> {outputPath}", "", 0));
    }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

internal static class ControllerFactory
{
    /// <summary>Build a ConversionController with the client_id pre-injected in HttpContext.Items.</summary>
    public static ConversionController Create(
        IJobManager jobManager,
        IConversionService conversionService,
        string clientId = "acme")
    {
        var controller = new ConversionController(
            jobManager,
            conversionService,
            NullLogger<ConversionController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        httpContext.Items["client_id"] = clientId;

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    /// <summary>Build a ConversionController WITHOUT client_id (simulates auth failure).</summary>
    public static ConversionController CreateUnauthenticated(
        IJobManager jobManager,
        IConversionService conversionService)
    {
        var controller = new ConversionController(
            jobManager,
            conversionService,
            NullLogger<ConversionController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        // No client_id in Items

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }
}

// ── POST /v1/migrate tests ────────────────────────────────────────────────────

public class MigrateEndpointTests
{
    private readonly JobManager _jobManager;
    private readonly MockConversionService _mockConversion;

    public MigrateEndpointTests()
    {
        _jobManager = new JobManager();
        _mockConversion = new MockConversionService();
    }

    [Fact]
    public void Migrate_ValidRequest_Returns202WithJobId()
    {
        var controller = ControllerFactory.Create(_jobManager, _mockConversion);
        var request = new MigrateRequest
        {
            SourcePath = "/data/report.twb",
            OutputPath = "/output/report.pbix",
        };

        var result = controller.Migrate(request) as AcceptedResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);

        var response = result.Value as MigrateResponse;
        Assert.NotNull(response);
        Assert.False(string.IsNullOrWhiteSpace(response.JobId));
        Assert.Equal("pending", response.Status);
    }

    [Fact]
    public void Migrate_NoClientId_Returns401()
    {
        var controller = ControllerFactory.CreateUnauthenticated(_jobManager, _mockConversion);
        var request = new MigrateRequest
        {
            SourcePath = "/data/report.twb",
            OutputPath = "/output/report.pbix",
        };

        var result = controller.Migrate(request) as UnauthorizedObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public void Migrate_InvalidModel_Returns400()
    {
        var controller = ControllerFactory.Create(_jobManager, _mockConversion);
        controller.ModelState.AddModelError("source_path", "Required");

        var request = new MigrateRequest { SourcePath = "", OutputPath = "/output/report.pbix" };

        var result = controller.Migrate(request) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public void Migrate_CreatesJobInJobManager()
    {
        var controller = ControllerFactory.Create(_jobManager, _mockConversion, clientId: "acme");
        var request = new MigrateRequest
        {
            SourcePath = "/data/test.twb",
            OutputPath = "/output/test.pbix",
        };

        var result = controller.Migrate(request) as AcceptedResult;
        Assert.NotNull(result);

        var response = result.Value as MigrateResponse;
        Assert.NotNull(response);

        // Job must be visible to same client (DA-014)
        var job = _jobManager.GetJob(response.JobId, "acme");
        Assert.NotNull(job);
        Assert.Equal("acme", job.ClientId);
    }
}

// ── GET /v1/status/{jobId} tests ──────────────────────────────────────────────

public class GetStatusEndpointTests
{
    private readonly JobManager _jobManager;
    private readonly MockConversionService _mockConversion;

    public GetStatusEndpointTests()
    {
        _jobManager = new JobManager();
        _mockConversion = new MockConversionService();
    }

    [Fact]
    public void GetStatus_ValidJobId_ReturnsCorrectStatus()
    {
        // Arrange: create a job for "acme"
        var jobId = _jobManager.CreateJob("acme");

        var controller = ControllerFactory.Create(_jobManager, _mockConversion, clientId: "acme");

        var result = controller.GetStatus(jobId) as OkObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);

        var response = result.Value as JobStatusResponse;
        Assert.NotNull(response);
        Assert.Equal(jobId, response.JobId);
        Assert.Equal("pending", response.Status);
    }

    [Fact]
    public void GetStatus_UnknownJobId_Returns404()
    {
        var controller = ControllerFactory.Create(_jobManager, _mockConversion);

        var result = controller.GetStatus("00000000-0000-0000-0000-000000000000") as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }

    [Fact]
    public void GetStatus_NoClientId_Returns401()
    {
        var controller = ControllerFactory.CreateUnauthenticated(_jobManager, _mockConversion);

        var result = controller.GetStatus("some-job-id") as UnauthorizedObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    /// <summary>
    /// DA-014: client "other" must NOT see job created by "acme".
    /// </summary>
    [Fact]
    public void GetStatus_OtherClientJob_Returns404_DA014()
    {
        // Create job for "acme"
        var jobId = _jobManager.CreateJob("acme");

        // "other-client" tries to access acme's job
        var controller = ControllerFactory.Create(_jobManager, _mockConversion, clientId: "other-client");

        var result = controller.GetStatus(jobId) as NotFoundObjectResult;

        // DA-014: must return 404, not the real status (no information leak)
        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }
}

// ── GET /v1/result/{jobId} tests ──────────────────────────────────────────────

public class GetResultEndpointTests
{
    private readonly JobManager _jobManager;
    private readonly MockConversionService _mockConversion;

    public GetResultEndpointTests()
    {
        _jobManager = new JobManager();
        _mockConversion = new MockConversionService();
    }

    [Fact]
    public void GetResult_CompletedJob_Returns200()
    {
        var jobId = _jobManager.CreateJob("acme");
        _jobManager.UpdateJob(jobId, "acme", JobStatus.Completed, result: "/output/done.pbix");

        var controller = ControllerFactory.Create(_jobManager, _mockConversion, clientId: "acme");

        var result = controller.GetResult(jobId) as OkObjectResult;

        Assert.NotNull(result);
        var response = result.Value as JobResultResponse;
        Assert.NotNull(response);
        Assert.Equal("completed", response.Status);
        Assert.Equal("/output/done.pbix", response.OutputPath);
    }

    [Fact]
    public void GetResult_PendingJob_Returns409()
    {
        var jobId = _jobManager.CreateJob("acme");

        var controller = ControllerFactory.Create(_jobManager, _mockConversion, clientId: "acme");

        var result = controller.GetResult(jobId) as ConflictObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
    }

    [Fact]
    public void GetResult_UnknownJob_Returns404()
    {
        var controller = ControllerFactory.Create(_jobManager, _mockConversion);

        var result = controller.GetResult("ghost-job-id") as NotFoundObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
    }
}
