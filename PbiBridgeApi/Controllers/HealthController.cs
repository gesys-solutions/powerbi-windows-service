using Microsoft.AspNetCore.Mvc;

namespace PbiBridgeApi.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    [HttpGet("/health")]
    public IActionResult Get()
        => Ok(new
        {
            status = "ok",
            service = "powerbi-windows-validator",
            role = "validation-only",
            version = "2.0.0"
        });
}
