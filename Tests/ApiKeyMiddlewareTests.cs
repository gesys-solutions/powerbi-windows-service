using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PbiBridgeApi.Middleware;
using PbiBridgeApi.Services;

namespace PbiBridgeApi.Tests;

public class InMemoryApiKeyStoreTests
{
    [Fact]
    public void RegisterAndResolveClient_ShouldWork()
    {
        var store = new InMemoryApiKeyStore();
        var registered = store.RegisterClient("client-1", "key-abc");
        Assert.True(registered);

        var found = store.TryGetClientId("key-abc", out var clientId);
        Assert.True(found);
        Assert.Equal("client-1", clientId);
    }

    [Fact]
    public void RegisterDuplicateClientId_ShouldFail()
    {
        var store = new InMemoryApiKeyStore();
        store.RegisterClient("client-1", "key-abc");
        Assert.False(store.RegisterClient("client-1", "key-xyz"));
    }

    [Fact]
    public void RevokeClient_ShouldRemoveKey()
    {
        var store = new InMemoryApiKeyStore();
        store.RegisterClient("client-2", "key-def");
        Assert.True(store.RevokeClient("client-2"));
        Assert.False(store.TryGetClientId("key-def", out _));
    }
}

public class ApiKeyMiddlewareTests
{
    private const string AdminKey = "test-admin-key-12345";

    private ApiKeyMiddleware BuildMiddleware(RequestDelegate next, IApiKeyStore? store = null)
    {
        store ??= new InMemoryApiKeyStore();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ADMIN_API_KEY"] = AdminKey })
            .Build();
        return new ApiKeyMiddleware(next, config, store, NullLogger<ApiKeyMiddleware>.Instance);
    }

    private static DefaultHttpContext MakeContext(
        string path,
        string method = "GET",
        string? apiKey = null,
        string? adminKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        if (apiKey is not null)
            ctx.Request.Headers["X-API-Key"] = apiKey;
        if (adminKey is not null)
            ctx.Request.Headers["X-Admin-Key"] = adminKey;
        return ctx;
    }

    [Fact]
    public async Task HealthEndpoint_GetOnly_ShouldBypassAuth()
    {
        var called = false;
        var mw = BuildMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        });

        var ctx = MakeContext("/health", method: "GET");
        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_Requires_XAdminKey()
    {
        var mw = BuildMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/admin/clients");

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_XApiKeyOnly_ShouldNotPass()
    {
        var mw = BuildMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/admin/clients", apiKey: AdminKey);

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task AdminRoute_ValidXAdminKey_ShouldPass_AndInjectAdminClientId()
    {
        string? clientId = null;
        var mw = BuildMiddleware(ctx =>
        {
            clientId = ctx.Items["client_id"]?.ToString();
            return Task.CompletedTask;
        });

        var ctx = MakeContext("/admin/clients", adminKey: AdminKey);
        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("__admin__", clientId);
    }

    [Fact]
    public async Task MissingApiKey_OnValidationRoute_ShouldReturn401()
    {
        var mw = BuildMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/v1/validate", method: "POST");

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ValidClientKey_ShouldPass_AndInjectClientId()
    {
        var store = new InMemoryApiKeyStore();
        store.RegisterClient("acme", "acme-key-999");

        string? clientId = null;
        var mw = BuildMiddleware(ctx =>
        {
            clientId = ctx.Items["client_id"]?.ToString();
            return Task.CompletedTask;
        }, store);

        var ctx = MakeContext("/v1/validate", method: "POST", apiKey: "acme-key-999");
        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("acme", clientId);
    }

    [Fact]
    public async Task AdminKey_ShouldPass_OnValidationRoute_WhenUsingXAdminKey()
    {
        string? clientId = null;
        var mw = BuildMiddleware(ctx =>
        {
            clientId = ctx.Items["client_id"]?.ToString();
            return Task.CompletedTask;
        });

        var ctx = MakeContext("/v1/validation-status/123", adminKey: AdminKey);
        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("__admin__", clientId);
    }

    [Fact]
    public void MissingAdminApiKeyEnvVar_ShouldThrowOnConstruction()
    {
        var config = new ConfigurationBuilder().Build();
        var store = new InMemoryApiKeyStore();
        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyMiddleware(_ => Task.CompletedTask, config, store, NullLogger<ApiKeyMiddleware>.Instance));
    }
}
