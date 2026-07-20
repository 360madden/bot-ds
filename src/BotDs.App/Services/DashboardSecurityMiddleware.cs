namespace BotDs.App.Services;

public sealed class DashboardSecurityMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _apiToken;
    private readonly string _controlToken;
    private readonly ILogger<DashboardSecurityMiddleware> _log;

    private static readonly string[] AllowedHosts = ["localhost", "127.0.0.1", "::1"];

    public DashboardSecurityMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<DashboardSecurityMiddleware> log)
    {
        _next = next;
        _apiToken = configuration.GetValue<string>("BotDs:Dashboard:ApiToken") ?? string.Empty;
        _controlToken = configuration.GetValue<string>("BotDs:Dashboard:ControlToken") ?? string.Empty;
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

        string? authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        string? controlHeader = context.Request.Headers["X-Control-Token"].FirstOrDefault();
        string? bearerToken = ExtractBearerToken(authHeader);

        bool isControl = IsControlPath(context.Request.Path, context.Request.Method);

        if (isControl)
        {
            if (IsValidControlToken(controlHeader) || IsValidControlToken(bearerToken))
            {
                await _next(context);
                return;
            }
            _log.LogWarning("Unauthorized control request to {Path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (IsValidApiToken(bearerToken) || IsValidControlToken(bearerToken) || IsValidControlToken(controlHeader))
        {
            await _next(context);
            return;
        }

        _log.LogWarning("Unauthorized API request to {Path}", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private bool IsValidApiToken(string? token) =>
        !string.IsNullOrEmpty(_apiToken) && string.Equals(token, _apiToken, StringComparison.Ordinal);

    private bool IsValidControlToken(string? token) =>
        !string.IsNullOrEmpty(_controlToken) && string.Equals(token, _controlToken, StringComparison.Ordinal);

    private static bool IsLoopbackAddress(System.Net.IPAddress? address)
    {
        if (address is null)
            return false;
        return System.Net.IPAddress.IsLoopback(address);
    }

    private static bool IsControlPath(PathString path, string method)
    {
        if (path.StartsWithSegments("/api/control", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
            && path.StartsWithSegments("/api/profiles/reload", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
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

    private static string? ExtractBearerToken(string? authHeader)
    {
        if (string.IsNullOrEmpty(authHeader))
            return null;
        const string prefix = "Bearer ";
        if (authHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return authHeader[prefix.Length..].Trim();
        return null;
    }
}
