using PbiBridgeApi.Controllers;

var builder = WebApplication.CreateBuilder(args);

// DA-012: Port 8090 ONLY — never change
builder.WebHost.UseUrls("http://0.0.0.0:8090");

// Windows Service support (ADR-002)
builder.Host.UseWindowsService();

builder.Services.AddControllers();

var app = builder.Build();

app.UseHttpsRedirection();
app.MapControllers();

app.Run();
