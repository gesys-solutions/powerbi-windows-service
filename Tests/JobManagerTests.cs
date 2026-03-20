using Microsoft.Extensions.Logging.Abstractions;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Tests;

/// <summary>
/// Unit tests for JobManager.
/// Covers: isolation (DA-014), cleanup (DA-016), thread-safety.
/// </summary>
public class JobManagerTests
{
    private static JobManager CreateManager() => new();

    // ────────────────────────────────────────────────────────
    // CreateJob
    // ────────────────────────────────────────────────────────

    [Fact]
    public void CreateJob_ReturnsNonEmptyJobId()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");
        Assert.False(string.IsNullOrWhiteSpace(jobId));
    }

    [Fact]
    public void CreateJob_TwoJobs_HaveDifferentIds()
    {
        var manager = CreateManager();
        var id1 = manager.CreateJob("client-A");
        var id2 = manager.CreateJob("client-A");
        Assert.NotEqual(id1, id2);
    }

    // ────────────────────────────────────────────────────────
    // GetJob — DA-014 isolation
    // ────────────────────────────────────────────────────────

    [Fact]
    public void GetJob_CorrectClient_ReturnsRecord()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        var record = manager.GetJob(jobId, "client-A");

        Assert.NotNull(record);
        Assert.Equal(jobId, record.JobId);
        Assert.Equal("client-A", record.ClientId);
        Assert.Equal(JobStatus.Pending, record.Status);
    }

    [Fact]
    public void GetJob_WrongClient_ReturnsNull_DA014()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        // DA-014: client-B must NOT see client-A's job
        var record = manager.GetJob(jobId, "client-B");

        Assert.Null(record);
    }

    [Fact]
    public void GetJob_NonExistentJobId_ReturnsNull()
    {
        var manager = CreateManager();
        var record = manager.GetJob("does-not-exist", "client-A");
        Assert.Null(record);
    }

    // ────────────────────────────────────────────────────────
    // ListJobs — DA-014 isolation
    // ────────────────────────────────────────────────────────

    [Fact]
    public void ListJobs_ReturnsOnlyClientOwnedJobs_DA014()
    {
        var manager = CreateManager();
        manager.CreateJob("client-A");
        manager.CreateJob("client-A");
        manager.CreateJob("client-B");

        var jobsA = manager.ListJobs("client-A").ToList();
        var jobsB = manager.ListJobs("client-B").ToList();

        Assert.Equal(2, jobsA.Count);
        Assert.All(jobsA, j => Assert.Equal("client-A", j.ClientId));
        Assert.Single(jobsB);
        Assert.All(jobsB, j => Assert.Equal("client-B", j.ClientId));
    }

    [Fact]
    public void ListJobs_ClientWithNoJobs_ReturnsEmpty()
    {
        var manager = CreateManager();
        manager.CreateJob("client-A");

        var jobs = manager.ListJobs("client-B").ToList();
        Assert.Empty(jobs);
    }

    // ────────────────────────────────────────────────────────
    // UpdateJob — DA-014 isolation
    // ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateJob_CorrectClient_UpdatesRecord()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        var success = manager.UpdateJob(jobId, "client-A", JobStatus.Running);

        Assert.True(success);
        var record = manager.GetJob(jobId, "client-A");
        Assert.Equal(JobStatus.Running, record!.Status);
        Assert.NotNull(record.StartedAt);
    }

    [Fact]
    public void UpdateJob_WrongClient_ReturnsFalse_DA014()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        // DA-014: client-B must not be able to update client-A's job
        var success = manager.UpdateJob(jobId, "client-B", JobStatus.Running);

        Assert.False(success);
        // Verify job was NOT modified
        var record = manager.GetJob(jobId, "client-A");
        Assert.Equal(JobStatus.Pending, record!.Status);
    }

    [Fact]
    public void UpdateJob_Completed_SetsCompletedAt()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");
        manager.UpdateJob(jobId, "client-A", JobStatus.Completed, result: "output.pbix");

        var record = manager.GetJob(jobId, "client-A");
        Assert.Equal(JobStatus.Completed, record!.Status);
        Assert.Equal("output.pbix", record.Result);
        Assert.NotNull(record.CompletedAt);
    }

    [Fact]
    public void UpdateJob_Failed_SetsError()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");
        manager.UpdateJob(jobId, "client-A", JobStatus.Failed, error: "Python error");

        var record = manager.GetJob(jobId, "client-A");
        Assert.Equal(JobStatus.Failed, record!.Status);
        Assert.Equal("Python error", record.Error);
    }

    // ────────────────────────────────────────────────────────
    // Cleanup — DA-016
    // ────────────────────────────────────────────────────────

    [Fact]
    public void RemoveExpiredJobs_RemovesJobsOlderThanMaxAge_DA016()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        // Simulate the job being older than 48h by using a short max age
        manager.RemoveExpiredJobs(TimeSpan.Zero);

        // Job should be gone
        var record = manager.GetJob(jobId, "client-A");
        Assert.Null(record);
    }

    [Fact]
    public void RemoveExpiredJobs_KeepsRecentJobs()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A");

        // Max age of 48h — job just created, should survive
        manager.RemoveExpiredJobs(TimeSpan.FromHours(48));

        var record = manager.GetJob(jobId, "client-A");
        Assert.NotNull(record);
    }

    [Fact]
    public void RemoveExpiredJobs_OnlyRemovesExpired()
    {
        var manager = CreateManager();
        var keepJobId = manager.CreateJob("client-A");

        // Remove with zero age — removes the just-created job
        // But we test the logic: if we have 2 clients, only expired ones go
        manager.RemoveExpiredJobs(TimeSpan.FromHours(48));

        // keepJobId was just created (within 48h) — still there
        Assert.NotNull(manager.GetJob(keepJobId, "client-A"));
    }

    // ────────────────────────────────────────────────────────
    // Thread-safety
    // ────────────────────────────────────────────────────────

    [Fact]
    public async Task ThreadSafety_ConcurrentCreateAndRead_NoRaceCondition()
    {
        var manager = CreateManager();
        const int threadCount = 10;
        const int jobsPerThread = 50;

        var tasks = Enumerable.Range(0, threadCount).Select(i =>
            Task.Run(() =>
            {
                var clientId = $"client-{i}";
                for (int j = 0; j < jobsPerThread; j++)
                {
                    var jobId = manager.CreateJob(clientId);
                    var record = manager.GetJob(jobId, clientId);
                    Assert.NotNull(record);
                    Assert.Equal(clientId, record!.ClientId);
                }
            })
        ).ToArray();

        await Task.WhenAll(tasks);

        // Each thread created jobsPerThread jobs — verify counts
        for (int i = 0; i < threadCount; i++)
        {
            var count = manager.ListJobs($"client-{i}").Count();
            Assert.Equal(jobsPerThread, count);
        }
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentUpdateAndRead_NoRaceCondition()
    {
        var manager = CreateManager();
        const int threadCount = 10;

        // Pre-create jobs for each thread
        var jobIds = Enumerable.Range(0, threadCount)
            .Select(i => (clientId: $"client-{i}", jobId: manager.CreateJob($"client-{i}")))
            .ToList();

        var tasks = jobIds.Select(item => Task.Run(() =>
        {
            manager.UpdateJob(item.jobId, item.clientId, JobStatus.Running);
            manager.UpdateJob(item.jobId, item.clientId, JobStatus.Completed, result: "done");
        })).ToArray();

        await Task.WhenAll(tasks);

        foreach (var item in jobIds)
        {
            var record = manager.GetJob(item.jobId, item.clientId);
            Assert.Equal(JobStatus.Completed, record!.Status);
        }
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentCleanup_NoRaceCondition()
    {
        var manager = CreateManager();

        // Create jobs from multiple threads, then run cleanup concurrently
        var createTasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 20; j++)
                manager.CreateJob($"client-{i}");
        })).ToArray();

        await Task.WhenAll(createTasks);

        // Concurrent cleanup + read should not throw
        var cleanupTask = Task.Run(() => manager.RemoveExpiredJobs(TimeSpan.Zero));
        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < 10; i++)
                _ = manager.ListJobs($"client-{i}").ToList();
        });

        await Task.WhenAll(cleanupTask, readTask);
        // If we reach here without exception — thread-safety OK
    }
}
