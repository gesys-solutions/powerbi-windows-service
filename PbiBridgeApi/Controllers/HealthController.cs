using Microsoft.AspNetCore.Mvc;

namespace PbiBridgeApi.Controllers;

[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    // DA-013: /health is exempt from X-API-Key auth
    [HttpGet("/health")]
    public IActionResult Get()
    {
        return Ok(new { status = "ok", version = "1.0.0" });
    }
}
