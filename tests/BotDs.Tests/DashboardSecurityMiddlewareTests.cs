using System.Net;
using BotDs.App.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

public sealed class DashboardSecurityMiddlewareTests
{
    private const string ApiToken = "test-api-token";
    private const string ControlToken = "test-control-token";

    [Fact]
    public async Task RemoteIp_RejectsNonLoopback()
    {
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Parse("192.168.1.100"),
            bearerToken: ApiToken);

        var middleware = CreateMiddleware();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task NullRemoteIp_Rejected()
    {
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: null,
            bearerToken: ApiToken);

        var middleware = CreateMiddleware();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonLocalHostHeader_Rejected()
    {
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Loopback,
            bearerToken: ApiToken,
            host: "example.test");

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task CrossOriginRequest_Rejected()
    {
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Loopback,
            bearerToken: ApiToken);
        context.Request.Headers.Origin = "http://localhost:5001";

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task LocalApi_AcceptsValidApiToken()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Loopback,
            bearerToken: ApiToken);

        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task ReloadEndpoint_RejectsApiToken()
    {
        var context = CreateHttpContext(
            path: "/api/profiles/reload",
            method: "POST",
            remoteIp: IPAddress.Loopback,
            bearerToken: ApiToken);

        var middleware = CreateMiddleware();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task ControlEndpoint_RejectsApiToken()
    {
        var context = CreateHttpContext(
            path: "/api/control/arm",
            method: "POST",
            remoteIp: IPAddress.Loopback,
            bearerToken: ApiToken);

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReloadEndpoint_AcceptsControlToken()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(
            path: "/api/profiles/reload",
            method: "POST",
            remoteIp: IPAddress.Loopback,
            bearerToken: ControlToken);

        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task EmptyToken_FailClosed()
    {
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Loopback,
            bearerToken: null);

        var middleware = CreateMiddleware();
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task IPv4MappedLoopback_Accepted()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(
            path: "/api/status",
            remoteIp: IPAddress.Parse("::ffff:127.0.0.1"),
            bearerToken: ApiToken);

        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    private static DashboardSecurityMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Dashboard:ApiToken"] = ApiToken,
                ["BotDs:Dashboard:ControlToken"] = ControlToken,
            })
            .Build();

        return new DashboardSecurityMiddleware(
            next ?? (_ => Task.CompletedTask),
            config,
            NullLogger<DashboardSecurityMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(
        string path,
        string method = "GET",
        IPAddress? remoteIp = null,
        string? bearerToken = null,
        string? host = "localhost",
        int port = 5000)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Request.Host = new HostString($"{host}:{port}");
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = remoteIp;

        if (bearerToken is not null)
            context.Request.Headers.Authorization = $"Bearer {bearerToken}";

        return context;
    }
}
