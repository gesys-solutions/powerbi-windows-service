using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PbiBridgeApi.Services;

/// <summary>
/// In-memory, thread-safe job manager.
/// DA-014: strict isolation by client_id.
/// DA-016: automatic cleanup of jobs older than 48h via background service.
/// </summary>
public sealed class JobManager : IJobManager
{
    // jobId -> JobRecord
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);

    public string CreateJob(string clientId)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var record = new JobRecord
        {
            JobId = jobId,
            ClientId = clientId,
            Status = JobStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };
        _jobs[jobId] = record;
        return jobId;
    }

    /// <summary>
    /// DA-014: Returns null for both "not found" and "client mismatch" — no information leakage.
    /// </summary>
    public JobRecord? GetJob(string jobId, string clientId)
    {
        if (!_jobs.TryGetValue(jobId, out var record))
            return null;

        // Strict isolation: return null if client_id does not match
        return record.ClientId == clientId ? record : null;
    }

    /// <summary>DA-014: only returns jobs owned by clientId.</summary>
    public IEnumerable<JobRecord> ListJobs(string clientId)
        => _jobs.Values.Where(j => j.ClientId == clientId).ToList();

    /// <summary>DA-014: update only if job exists and belongs to clientId.</summary>
    public bool UpdateJob(string jobId, string clientId, JobStatus status,
        string? result = null, string? error = null)
    {
        if (!_jobs.TryGetValue(jobId, out var record))
            return false;

        // Strict isolation: reject if client_id does not match
        if (record.ClientId != clientId)
            return false;

        record.Status = status;
        record.Result = result;
        record.Error = error;

        if (status == JobStatus.Running && record.StartedAt is null)
            record.StartedAt = DateTime.UtcNow;

        if (status is JobStatus.Completed or JobStatus.Failed)
            record.CompletedAt = DateTime.UtcNow;

        return true;
    }

    /// <summary>
    /// DA-016: Remove all jobs older than 48h. Called by JobCleanupService.
    /// </summary>
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

/// <summary>
/// Background hosted service that cleans up jobs older than 48h every hour.
/// DA-016: automatic cleanup — no memory leak.
/// </summary>
public sealed class JobCleanupService : BackgroundService
{
    private static readonly TimeSpan MaxJobAge = TimeSpan.FromHours(48);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly JobManager _jobManager;
    private readonly ILogger<JobCleanupService> _logger;

    public JobCleanupService(IJobManager jobManager, ILogger<JobCleanupService> logger)
    {
        // We depend on the concrete type to access internal cleanup method.
        // JobManager is registered as both IJobManager and JobManager (same singleton).
        _jobManager = (JobManager)jobManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobCleanupService started. Cleanup interval: {Interval}h, max age: {MaxAge}h",
            CleanupInterval.TotalHours, MaxJobAge.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
                _jobManager.RemoveExpiredJobs(MaxJobAge);
                _logger.LogInformation("Job cleanup completed at {Now}", DateTime.UtcNow);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job cleanup error");
            }
        }

        _logger.LogInformation("JobCleanupService stopped.");
    }
}
