using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace PbiBridgeApi.Services;

public sealed class ValidationJobManager : IValidationJobManager
{
    private const string AdminClientId = "__admin__";

    private readonly ConcurrentDictionary<string, ValidationJobRecord> _jobs = new(StringComparer.Ordinal);

    public string CreateJob(string clientId, string artifactPath, string validator)
    {
        var jobId = Guid.NewGuid().ToString("N");
        _jobs[jobId] = new ValidationJobRecord
        {
            JobId = jobId,
            ClientId = clientId,
            ArtifactPath = artifactPath,
            Validator = validator,
            Status = ValidationStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        };
        return jobId;
    }

    public ValidationJobRecord? GetJob(string jobId, string requesterClientId, bool allowAdminOverride = false)
    {
        if (!_jobs.TryGetValue(jobId, out var record))
            return null;

        if (string.Equals(record.ClientId, requesterClientId, StringComparison.Ordinal))
            return record;

        if (allowAdminOverride && string.Equals(requesterClientId, AdminClientId, StringComparison.Ordinal))
            return record;

        return null;
    }

    public IEnumerable<ValidationJobRecord> ListJobs(string clientId)
        => _jobs.Values.Where(job => job.ClientId == clientId).ToList();

    public bool UpdateJob(
        string jobId,
        string clientId,
        ValidationStatus status,
        string? error = null,
        ValidationReportDocument? report = null)
    {
        if (!_jobs.TryGetValue(jobId, out var record))
            return false;

        if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
            return false;

        record.Status = status;
        record.Error = error;
        record.Report = report;

        if (status == ValidationStatus.Running && record.StartedAt is null)
            record.StartedAt = DateTime.UtcNow;

        if (status.IsTerminal())
            record.CompletedAt = DateTime.UtcNow;

        return true;
    }

    internal void RemoveExpiredJobs(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var key in _jobs.Keys.ToList())
        {
            if (_jobs.TryGetValue(key, out var record) && record.CreatedAt < cutoff)
                _jobs.TryRemove(key, out _);
        }
    }
}

public sealed class ValidationJobCleanupService : BackgroundService
{
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(48);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly ValidationJobManager _jobManager;
    private readonly ILogger<ValidationJobCleanupService> _logger;

    public ValidationJobCleanupService(IValidationJobManager jobManager, ILogger<ValidationJobCleanupService> logger)
    {
        _jobManager = (ValidationJobManager)jobManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ValidationJobCleanupService started. Cleanup interval: {Interval}h, max age: {MaxAge}h",
            CleanupInterval.TotalHours,
            MaxJobAge.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
                _jobManager.RemoveExpiredJobs(MaxJobAge);
                _logger.LogInformation("Validation job cleanup completed at {Now}", DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation job cleanup error");
            }
        }

        _logger.LogInformation("ValidationJobCleanupService stopped.");
    }
}
