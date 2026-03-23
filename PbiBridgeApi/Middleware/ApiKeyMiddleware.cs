using PbiBridgeApi.Services;

namespace PbiBridgeApi.Middleware;

/// <summary>
/// Auth contract:
/// - GET /health: anonymous
/// - /admin/*: X-Admin-Key
/// - POST /v1/validate: X-API-Key (client)
/// - GET /v1/validation-status|validation-report: X-API-Key (client) or X-Admin-Key (operator diagnostic, read-only)
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string AdminKeyHeader = "X-Admin-Key";
    private const string ClientIdKey = "client_id";
    private const string AdminClientId = "__admin__";

    private readonly RequestDelegate _next;
    private readonly string _adminApiKey;
    private readonly IApiKeyStore _store;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        IApiKeyStore store,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
        _adminApiKey = configuration["ADMIN_API_KEY"]
            ?? throw new InvalidOperationException("ADMIN_API_KEY environment variable is required and must not be empty.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        if (HttpMethods.IsGet(method) && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (TryAuthenticateAdmin(context))
        {
            await _next(context);
            return;
        }

        if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Missing or invalid X-Admin-Key for {Method} {Path}", method, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Admin-Key header" });
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var extractedKey)
            || string.IsNullOrWhiteSpace(extractedKey))
        {
            _logger.LogWarning("Missing X-API-Key for {Method} {Path}", method, path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing X-API-Key header" });
            return;
        }

        var key = extractedKey.ToString();
        if (_store.TryGetClientId(key, out var clientId))
        {
            context.Items[ClientIdKey] = clientId;
            await _next(context);
            return;
        }

        _logger.LogWarning("Invalid X-API-Key for {Method} {Path}", method, path);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid X-API-Key" });
    }

    private bool TryAuthenticateAdmin(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(AdminKeyHeader, out var extractedKey)
            || string.IsNullOrWhiteSpace(extractedKey))
        {
            return false;
        }

        if (!string.Equals(extractedKey.ToString(), _adminApiKey, StringComparison.Ordinal))
        {
            return false;
        }

        context.Items[ClientIdKey] = AdminClientId;
        return true;
    }
}
