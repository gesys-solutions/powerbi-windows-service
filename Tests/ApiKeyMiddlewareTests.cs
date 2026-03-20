using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PbiBridgeApi.Middleware;
using PbiBridgeApi.Services;
using Xunit;

namespace PbiBridgeApi.Tests;

// ── InMemoryApiKeyStore unit tests ────────────────────────────────────────────

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
        var duplicate = store.RegisterClient("client-1", "key-xyz");
        Assert.False(duplicate);
    }

    [Fact]
    public void RevokeClient_ShouldRemoveKey()
    {
        var store = new InMemoryApiKeyStore();
        store.RegisterClient("client-2", "key-def");
        var revoked = store.RevokeClient("client-2");
        Assert.True(revoked);

        var found = store.TryGetClientId("key-def", out _);
        Assert.False(found);
    }

    [Fact]
    public void RevokeNonExistentClient_ShouldReturnFalse()
    {
        var store = new InMemoryApiKeyStore();
        var result = store.RevokeClient("ghost");
        Assert.False(result);
    }

    [Fact]
    public void ListClients_ShouldReturnAllRegistered()
    {
        var store = new InMemoryApiKeyStore();
        store.RegisterClient("c1", "key-111");
        store.RegisterClient("c2", "key-222");
        var list = store.ListClients().ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, c => c.ClientId == "c1");
        Assert.Contains(list, c => c.ClientId == "c2");
    }
}

// ── ApiKeyMiddleware unit tests ───────────────────────────────────────────────

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

    private static DefaultHttpContext MakeContext(string? path = "/v1/migrate", string? apiKey = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (apiKey is not null)
            ctx.Request.Headers["X-API-Key"] = apiKey;
        return ctx;
    }

    [Fact]
    public async Task HealthEndpoint_ShouldBypassAuth()
    {
        var called = false;
        var mw = BuildMiddleware(_ => { called = true; return Task.CompletedTask; });
        var ctx = MakeContext("/health");

        await mw.InvokeAsync(ctx);

        Assert.True(called);
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task MissingApiKey_ShouldReturn401()
    {
        var mw = BuildMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/v1/migrate");

        await mw.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidApiKey_ShouldReturn401()
    {
        var mw = BuildMiddleware(_ => Task.CompletedTask);
        var ctx = MakeContext("/v1/migrate", "bad-key");

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

        var ctx = MakeContext("/v1/migrate", "acme-key-999");
        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("acme", clientId);
    }

    [Fact]
    public async Task AdminKey_ShouldPass_WithAdminClientId()
    {
        string? clientId = null;
        var mw = BuildMiddleware(ctx =>
        {
            clientId = ctx.Items["client_id"]?.ToString();
            return Task.CompletedTask;
        });

        var ctx = MakeContext("/admin/clients", AdminKey);
        await mw.InvokeAsync(ctx);

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("__admin__", clientId);
    }

    [Fact]
    public void MissingAdminApiKeyEnvVar_ShouldThrowOnConstruction()
    {
        var config = new ConfigurationBuilder().Build(); // no ADMIN_API_KEY
        var store = new InMemoryApiKeyStore();
        Assert.Throws<InvalidOperationException>(() =>
            new ApiKeyMiddleware(
                _ => Task.CompletedTask,
                config,
                store,
                NullLogger<ApiKeyMiddleware>.Instance));
    }
}
