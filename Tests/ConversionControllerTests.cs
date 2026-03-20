using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    /// <summary>
    /// Workspace root used by all tests.
    /// Paths must start with {WorkspaceRoot}/{clientId}/ to pass path validation.
    /// </summary>
    public static readonly string TestWorkspaceRoot = "/tmp/pbi-test-workspaces";

    /// <summary>Build a ConversionController with the client_id pre-injected in HttpContext.Items.</summary>
    public static ConversionController Create(
        IJobManager jobManager,
        IConversionService conversionService,
        string clientId = "acme",
        string? workspaceRoot = null)
    {
        var opts = Options.Create(new ConversionOptions
        {
            WorkspaceRootPath = workspaceRoot ?? TestWorkspaceRoot,
        });

        var controller = new ConversionController(
            jobManager,
            conversionService,
            NullLogger<ConversionController>.Instance,
            opts);

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
        var opts = Options.Create(new ConversionOptions
        {
            WorkspaceRootPath = TestWorkspaceRoot,
        });

        var controller = new ConversionController(
            jobManager,
            conversionService,
            NullLogger<ConversionController>.Instance,
            opts);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        // No client_id in Items

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };

        return controller;
    }

    /// <summary>Build a valid source path for client under the test workspace.</summary>
    public static string ValidSourcePath(string clientId = "acme", string file = "report.twb")
        => $"{TestWorkspaceRoot}/{clientId}/{file}";

    /// <summary>Build a valid output path for client under the test workspace.</summary>
    public static string ValidOutputPath(string clientId = "acme", string file = "report.pbix")
        => $"{TestWorkspaceRoot}/{clientId}/output/{file}";
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
            SourcePath = ControllerFactory.ValidSourcePath(),
            OutputPath = ControllerFactory.ValidOutputPath(),
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
            SourcePath = ControllerFactory.ValidSourcePath(),
            OutputPath = ControllerFactory.ValidOutputPath(),
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

        var request = new MigrateRequest { SourcePath = "", OutputPath = ControllerFactory.ValidOutputPath() };

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
            SourcePath = ControllerFactory.ValidSourcePath("acme", "test.twb"),
            OutputPath = ControllerFactory.ValidOutputPath("acme", "test.pbix"),
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

// ── PathGuard unit tests (Blocker 1 — filesystem isolation) ──────────────────

public class PathGuardTests
{
    private const string WorkspaceRoot = "/tmp/pbi-test-workspaces";
    private const string ClientId = "acme";

    // ── Valid paths ──────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidPath_ReturnsTrue()
    {
        var (ok, err) = PathGuard.Validate(
            $"{WorkspaceRoot}/{ClientId}/input.twb",
            ClientId,
            WorkspaceRoot);

        Assert.True(ok, $"Expected valid — got error: {err}");
        Assert.Empty(err);
    }

    [Fact]
    public void Validate_ValidNestedPath_ReturnsTrue()
    {
        var (ok, err) = PathGuard.Validate(
            $"{WorkspaceRoot}/{ClientId}/subdir/deep/file.twb",
            ClientId,
            WorkspaceRoot);

        Assert.True(ok, $"Expected valid — got error: {err}");
    }

    // ── Path traversal attacks ───────────────────────────────────────────────

    [Fact]
    public void Validate_PathTraversal_DotDot_ReturnsFalse()
    {
        // ../evil would resolve to /tmp/pbi-test-workspaces/evil — outside acme sandbox
        var traversalPath = $"{WorkspaceRoot}/{ClientId}/../evil/file.twb";

        var (ok, err) = PathGuard.Validate(traversalPath, ClientId, WorkspaceRoot);

        Assert.False(ok, "Path traversal via .. must be rejected.");
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Validate_PathTraversal_EscapesWorkspace_ReturnsFalse()
    {
        // Tries to reach another client's workspace
        var traversalPath = $"{WorkspaceRoot}/{ClientId}/../../other-client/secret.twb";

        var (ok, err) = PathGuard.Validate(traversalPath, ClientId, WorkspaceRoot);

        Assert.False(ok, "Path traversal escaping workspace must be rejected.");
    }

    [Fact]
    public void Validate_SystemPath_ReturnsFalse()
    {
        // Absolute path outside the workspace entirely
        var (ok, err) = PathGuard.Validate("/etc/passwd", ClientId, WorkspaceRoot);

        Assert.False(ok, "System path must be rejected.");
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Validate_OtherClientPath_ReturnsFalse()
    {
        // Trying to access another client's sandbox directly
        var (ok, err) = PathGuard.Validate(
            $"{WorkspaceRoot}/other-client/file.twb",
            ClientId,     // ClientId = "acme"
            WorkspaceRoot);

        Assert.False(ok, "Another client's path must be rejected for the requesting client.");
    }

    // ── UNC paths ────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_UncPath_ReturnsFalse()
    {
        var (ok, err) = PathGuard.Validate(@"\\server\share\file.twb", ClientId, WorkspaceRoot);

        Assert.False(ok, "UNC path must be rejected.");
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Validate_UncPath_ForwardSlash_ReturnsFalse()
    {
        var (ok, err) = PathGuard.Validate("//server/share/file.twb", ClientId, WorkspaceRoot);

        Assert.False(ok, "UNC path (forward slash) must be rejected.");
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyPath_ReturnsFalse()
    {
        var (ok, err) = PathGuard.Validate("", ClientId, WorkspaceRoot);

        Assert.False(ok);
        Assert.NotEmpty(err);
    }

    [Fact]
    public void Validate_WhitespacePath_ReturnsFalse()
    {
        var (ok, err) = PathGuard.Validate("   ", ClientId, WorkspaceRoot);

        Assert.False(ok);
        Assert.NotEmpty(err);
    }

    // ── Controller integration: paths are validated before job creation ──────

    [Fact]
    public void Migrate_PathTraversal_Returns400()
    {
        var jobManager = new JobManager();
        var mockConversion = new MockConversionService();
        var controller = ControllerFactory.Create(jobManager, mockConversion, clientId: "acme");

        var request = new MigrateRequest
        {
            // Traversal: resolved path escapes the acme sandbox
            SourcePath = $"{ControllerFactory.TestWorkspaceRoot}/acme/../evil/file.twb",
            OutputPath = ControllerFactory.ValidOutputPath("acme"),
        };

        var result = controller.Migrate(request) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public void Migrate_ValidPathInsideSandbox_Returns202()
    {
        var jobManager = new JobManager();
        var mockConversion = new MockConversionService();
        var controller = ControllerFactory.Create(jobManager, mockConversion, clientId: "acme");

        var request = new MigrateRequest
        {
            SourcePath = ControllerFactory.ValidSourcePath("acme", "data.twb"),
            OutputPath = ControllerFactory.ValidOutputPath("acme", "data.pbix"),
        };

        var result = controller.Migrate(request) as AcceptedResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status202Accepted, result.StatusCode);
    }

    [Fact]
    public void Migrate_SystemPath_Returns400()
    {
        var jobManager = new JobManager();
        var mockConversion = new MockConversionService();
        var controller = ControllerFactory.Create(jobManager, mockConversion, clientId: "acme");

        var request = new MigrateRequest
        {
            SourcePath = "/etc/passwd",
            OutputPath = ControllerFactory.ValidOutputPath("acme"),
        };

        var result = controller.Migrate(request) as BadRequestObjectResult;

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }
}
