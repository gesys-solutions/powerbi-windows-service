using PbiBridgeApi.Services;

namespace PbiBridgeApi.Tests;

public class ValidationJobManagerTests
{
    private static ValidationJobManager CreateManager() => new();

    [Fact]
    public void CreateJob_ReturnsNonEmptyJobId_AndQueuedStatus()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A", "artifact.pbix", "contract-check");
        var record = manager.GetJob(jobId, "client-A");

        Assert.False(string.IsNullOrWhiteSpace(jobId));
        Assert.NotNull(record);
        Assert.Equal(ValidationStatus.Queued, record!.Status);
    }

    [Fact]
    public void GetJob_WrongClient_ReturnsNull()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A", "artifact.pbix", "contract-check");
        Assert.Null(manager.GetJob(jobId, "client-B"));
    }

    [Fact]
    public void GetJob_AdminOverride_ReturnsRecord_WhenExplicitlyAllowed()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A", "artifact.pbix", "contract-check");

        var record = manager.GetJob(jobId, "__admin__", allowAdminOverride: true);

        Assert.NotNull(record);
        Assert.Equal("client-A", record!.ClientId);
    }

    [Fact]
    public void ListJobs_ReturnsOnlyClientOwnedJobs()
    {
        var manager = CreateManager();
        manager.CreateJob("client-A", "a.pbix", "contract-check");
        manager.CreateJob("client-A", "b.pbix", "contract-check");
        manager.CreateJob("client-B", "c.pbix", "contract-check");

        var jobsA = manager.ListJobs("client-A").ToList();
        var jobsB = manager.ListJobs("client-B").ToList();

        Assert.Equal(2, jobsA.Count);
        Assert.Single(jobsB);
        Assert.All(jobsA, job => Assert.Equal("client-A", job.ClientId));
    }

    [Fact]
    public void UpdateJob_TerminalStatus_SetsReportAndCompletedAt()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A", "artifact.pbix", "contract-check");

        var success = manager.UpdateJob(
            jobId,
            "client-A",
            ValidationStatus.Failed,
            "Broken artifact.",
            new ValidationReportDocument
            {
                Summary = "Validation failed.",
                Error = "Broken artifact.",
                Checks = new List<ValidationCheckRecord>(),
            });

        var record = manager.GetJob(jobId, "client-A");
        Assert.True(success);
        Assert.NotNull(record);
        Assert.Equal(ValidationStatus.Failed, record!.Status);
        Assert.NotNull(record.CompletedAt);
        Assert.NotNull(record.Report);
    }

    [Fact]
    public void RemoveExpiredJobs_RemovesOldJobs()
    {
        var manager = CreateManager();
        var jobId = manager.CreateJob("client-A", "artifact.pbix", "contract-check");

        manager.RemoveExpiredJobs(TimeSpan.Zero);

        Assert.Null(manager.GetJob(jobId, "client-A"));
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentCreateAndRead_NoRaceCondition()
    {
        var manager = CreateManager();
        const int threadCount = 8;
        const int jobsPerThread = 25;

        var tasks = Enumerable.Range(0, threadCount).Select(i => Task.Run(() =>
        {
            var clientId = $"client-{i}";
            for (var j = 0; j < jobsPerThread; j++)
            {
                var jobId = manager.CreateJob(clientId, $"artifact-{j}.pbix", "contract-check");
                var record = manager.GetJob(jobId, clientId);
                Assert.NotNull(record);
                Assert.Equal(clientId, record!.ClientId);
            }
        }));

        await Task.WhenAll(tasks);

        for (var i = 0; i < threadCount; i++)
            Assert.Equal(jobsPerThread, manager.ListJobs($"client-{i}").Count());
    }
}
