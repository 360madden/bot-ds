namespace BotDs.App.Services;

/// <summary>
/// Local-only API gate: loopback remote address, localhost host header, and
/// same-origin checks. No token/auth system — personal local tool only.
/// </summary>
public sealed class DashboardSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DashboardSecurityMiddleware> _log;

    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "::1"];

    public DashboardSecurityMiddleware(
        RequestDelegate next,
        ILogger<DashboardSecurityMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!IsLoopbackAddress(context.Connection.RemoteIpAddress))
        {
            _log.LogWarning("Rejected non-loopback remote IP: {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        string host = context.Request.Host.Host;
        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            _log.LogWarning("Rejected non-localhost host: {Host}", host);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            string? origin = originValues.FirstOrDefault();
            if (!string.IsNullOrEmpty(origin) && !IsSameLocalOrigin(origin, context.Request))
            {
                _log.LogWarning("Rejected non-local origin: {Origin}", origin);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        await _next(context);
    }

    private static bool IsLoopbackAddress(System.Net.IPAddress? address)
    {
        if (address is null)
            return false;
        return System.Net.IPAddress.IsLoopback(address);
    }

    private static bool IsSameLocalOrigin(string origin, HttpRequest request)
    {
        try
        {
            var uri = new Uri(origin);
            int requestPort = request.Host.Port ?? (request.Scheme == "https" ? 443 : 80);
            return AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
                && uri.Port == requestPort;
        }
        catch
        {
            return false;
        }
    }
}
