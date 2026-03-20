using System.Runtime.CompilerServices;

// Allow test project to access internal members (e.g., JobManager.RemoveExpiredJobs)
[assembly: InternalsVisibleTo("PbiBridgeApi.Tests")]
