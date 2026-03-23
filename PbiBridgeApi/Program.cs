using PbiBridgeApi.Middleware;
using PbiBridgeApi.Services;

var builder = WebApplication.CreateBuilder(args);

// DA-012: port canonique réservé au validateur Windows
builder.WebHost.UseUrls("http://0.0.0.0:8090");

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}

builder.Services.AddControllers();
builder.Services.Configure<ValidationOptions>(builder.Configuration.GetSection("Validation"));

builder.Services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
builder.Services.AddSingleton<ValidationJobManager>();
builder.Services.AddSingleton<IValidationJobManager>(sp => sp.GetRequiredService<ValidationJobManager>());
builder.Services.AddHostedService<ValidationJobCleanupService>();
builder.Services.AddSingleton<IValidationService, ValidationService>();

var app = builder.Build();

app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();
app.Run();

public partial class Program;
