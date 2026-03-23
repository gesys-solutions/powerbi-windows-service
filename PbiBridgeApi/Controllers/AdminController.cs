using Microsoft.AspNetCore.Mvc;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Controllers;

/// <summary>
/// Admin endpoints for managing client API keys.
/// Protected by X-Admin-Key via ApiKeyMiddleware.
/// </summary>
[ApiController]
[Route("admin")]
public class AdminController : ControllerBase
{
    private readonly IApiKeyStore _store;
    private readonly ILogger<AdminController> _logger;

    public AdminController(IApiKeyStore store, ILogger<AdminController> logger)
    {
        _store = store;
        _logger = logger;
    }

    private bool IsAdmin()
        => HttpContext.Items.TryGetValue("client_id", out var cid) && cid?.ToString() == "__admin__";

    [HttpPost("clients")]
    public IActionResult RegisterClient([FromBody] RegisterClientRequest request)
    {
        if (!IsAdmin())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin access required" });

        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.ApiKey))
            return BadRequest(new { error = "client_id and api_key are required" });

        var success = _store.RegisterClient(request.ClientId, request.ApiKey);
        if (!success)
        {
            _logger.LogWarning("Failed to register client_id={ClientId} (duplicate)", request.ClientId);
            return Conflict(new { error = $"client_id '{request.ClientId}' already registered" });
        }

        _logger.LogInformation("Registered client_id={ClientId}", request.ClientId);
        return Ok(new { message = $"Client '{request.ClientId}' registered successfully" });
    }

    [HttpDelete("clients/{clientId}")]
    public IActionResult RevokeClient(string clientId)
    {
        if (!IsAdmin())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin access required" });

        var success = _store.RevokeClient(clientId);
        if (!success)
            return NotFound(new { error = $"client_id '{clientId}' not found" });

        _logger.LogInformation("Revoked client_id={ClientId}", clientId);
        return Ok(new { message = $"Client '{clientId}' revoked successfully" });
    }

    [HttpGet("clients")]
    public IActionResult ListClients()
    {
        if (!IsAdmin())
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Admin access required" });

        var clients = _store.ListClients()
            .Select(c => new { clientId = c.ClientId, maskedKey = c.MaskedKey })
            .ToList();

        return Ok(new { count = clients.Count, clients });
    }
}

public record RegisterClientRequest(string ClientId, string ApiKey);
