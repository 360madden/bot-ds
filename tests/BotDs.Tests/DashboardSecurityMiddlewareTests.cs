using System.Net;
using BotDs.App.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

/// <summary>
/// Loopback-only API gate tests. Token auth was removed (personal local tool).
/// </summary>
public sealed class DashboardSecurityMiddlewareTests
{
    [Fact]
    public async Task RemoteIp_RejectsNonLoopback()
    {
        var context = CreateHttpContext(path: "/api/status", remoteIp: IPAddress.Parse("192.168.1.100"));
        await CreateMiddleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task NullRemoteIp_Rejected()
    {
        var context = CreateHttpContext(path: "/api/status", remoteIp: null);
        await CreateMiddleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task NonLocalHostHeader_Rejected()
    {
        var context = CreateHttpContext(path: "/api/status", remoteIp: IPAddress.Loopback, host: "example.test");
        await CreateMiddleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task CrossOriginRequest_Rejected()
    {
        var context = CreateHttpContext(path: "/api/status", remoteIp: IPAddress.Loopback);
        context.Request.Headers.Origin = "http://localhost:5001";
        await CreateMiddleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task LocalApi_AllowedWithoutToken()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(path: "/api/status", remoteIp: IPAddress.Loopback);
        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task LocalControl_AllowedWithoutToken()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(path: "/api/control/arm", method: "POST", remoteIp: IPAddress.Loopback);
        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task LocalProfileReload_AllowedWithoutToken()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(path: "/api/profiles/reload", method: "POST", remoteIp: IPAddress.Loopback);
        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task NonApiPath_BypassesChecks()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(path: "/", remoteIp: IPAddress.Parse("8.8.8.8"));
        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task IPv6Loopback_Allowed()
    {
        bool nextCalled = false;
        var context = CreateHttpContext(path: "/api/status", remoteIp: IPAddress.IPv6Loopback, host: "localhost");
        var middleware = CreateMiddleware(next: _ => { nextCalled = true; return Task.CompletedTask; });
        await middleware.InvokeAsync(context);
        Assert.True(nextCalled);
    }

    private static DashboardSecurityMiddleware CreateMiddleware(RequestDelegate? next = null)
    {
        next ??= _ => Task.CompletedTask;
        return new DashboardSecurityMiddleware(next, NullLogger<DashboardSecurityMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(
        string path,
        IPAddress? remoteIp,
        string method = "GET",
        string host = "localhost")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Host = new HostString(host, 5068);
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = remoteIp;
        context.Response.Body = new MemoryStream();
        return context;
    }
}
