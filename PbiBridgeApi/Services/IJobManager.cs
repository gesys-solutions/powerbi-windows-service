namespace PbiBridgeApi.Services;

public interface IValidationJobManager
{
    string CreateJob(string clientId, string artifactPath, string validator);
    ValidationJobRecord? GetJob(string jobId, string requesterClientId, bool allowAdminOverride = false);
    IEnumerable<ValidationJobRecord> ListJobs(string clientId);
    bool UpdateJob(
        string jobId,
        string clientId,
        ValidationStatus status,
        string? error = null,
        ValidationReportDocument? report = null);
}
