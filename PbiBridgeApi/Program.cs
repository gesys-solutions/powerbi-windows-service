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

var app = builder.Build();

// DA-013: X-API-Key middleware — all routes except /health
app.UseMiddleware<ApiKeyMiddleware>();

app.MapControllers();

app.Run();
