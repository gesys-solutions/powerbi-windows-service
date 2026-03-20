using PbiBridgeApi.Middleware;
using PbiBridgeApi.Services;

var builder = WebApplication.CreateBuilder(args);

// DA-012: Port 8090 ONLY — never change
builder.WebHost.UseUrls("http://0.0.0.0:8090");

// Windows Service support (ADR-002)
builder.Host.UseWindowsService();

builder.Services.AddControllers();

// DA-017: ADMIN_API_KEY validation happens inside ApiKeyMiddleware constructor
// Register IApiKeyStore as singleton (in-memory, thread-safe ConcurrentDictionary)
builder.Services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();

// DA-014: JobManager — strict client_id isolation (a client never sees another's jobs)
// DA-016: JobCleanupService — automatic cleanup of jobs older than 48h
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<IJobManager>(sp => sp.GetRequiredService<JobManager>());
builder.Services.AddHostedService<JobCleanupService>();

// DA-015: ConversionService — calls tableau2pbi Python subprocess (never reimplements logic)
builder.Services.AddSingleton<IConversionService, ConversionService>();

var app = builder.Build();

// DA-013: X-API-Key middleware — all routes except /health
app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

app.Run();
