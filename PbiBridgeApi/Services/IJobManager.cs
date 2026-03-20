namespace PbiBridgeApi.Services;

/// <summary>
/// Thread-safe job manager with strict client isolation (DA-014) and 48h cleanup (DA-016).
/// </summary>
public interface IJobManager
{
    /// <summary>Create a new job for the given client. Returns the new job_id (GUID).</summary>
    string CreateJob(string clientId);

    /// <summary>
    /// Get a job by job_id. Returns null if not found OR if client_id does not match (DA-014).
    /// Never leaks information about other clients' jobs.
    /// </summary>
    JobRecord? GetJob(string jobId, string clientId);

    /// <summary>List all jobs belonging to the given client (DA-014: strict isolation).</summary>
    IEnumerable<JobRecord> ListJobs(string clientId);

    /// <summary>
    /// Update a job's status, result and error. Returns false if job not found or client mismatch (DA-014).
    /// </summary>
    bool UpdateJob(string jobId, string clientId, JobStatus status, string? result = null, string? error = null);
}
