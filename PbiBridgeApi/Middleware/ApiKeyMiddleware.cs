using PbiBridgeApi.Services;

namespace PbiBridgeApi.Middleware;

/// <summary>
/// Middleware that enforces X-API-Key authentication on all routes except GET /health.
/// DA-013: X-API-Key obligatoire sauf GET /health (GET uniquement, pas les autres verbes).
/// DA-017: ADMIN_API_KEY lu depuis variable d'environnement.
/// </summary>
public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";
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

        // DA-017: ADMIN_API_KEY must come from env var — never hardcoded
        _adminApiKey = configuration["ADMIN_API_KEY"]
            ?? throw new InvalidOperationException(
                "ADMIN_API_KEY environment variable is required and must not be empty.");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var method = context.Request.Method;

        // DA-013: only GET /health is exempt — other verbs on /health require auth
        if (HttpMethods.IsGet(method) &&
            path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
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

        // ADMIN_API_KEY: full access
        if (key == _adminApiKey)
        {
            context.Items[ClientIdKey] = AdminClientId;
            await _next(context);
            return;
        }

        // Client key lookup
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
}
