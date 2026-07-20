using BotDs.App.Endpoints;
using BotDs.App.Services;
using BotDs.Reader;
using Serilog;
using Serilog.Context;
using Serilog.Formatting.Compact;

string instanceId = Guid.NewGuid().ToString("N");
Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("InstanceId", instanceId)
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.WithProperty("Application", "BotDs")
        .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
        .Enrich.WithProperty("InstanceId", instanceId)
        .WriteTo.Console()
        .WriteTo.File(
            formatter: new RenderedCompactJsonFormatter(),
            path: Path.Combine("logs", "botds-.ndjson"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            flushToDiskInterval: TimeSpan.FromSeconds(1)));

    builder.Services.AddSingleton<SnapshotPublisher>();
    builder.Services.AddSingleton<ProfileService>();
    builder.Services.AddSingleton<ControllerStateMachine>();
    builder.Services.AddSingleton<EvaluatorLoop>();
    builder.Services.AddSingleton<ArmingReadinessService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EvaluatorLoop>());

    // ── Settings ─────────────────────────────────────────────
    string settingsPath = builder.Configuration.GetValue<string>("BotDs:Settings:FilePath")
        ?? Path.Combine("config", "botds-settings.json");
    string? settingsDir = Path.GetDirectoryName(settingsPath);
    if (!string.IsNullOrEmpty(settingsDir))
        Directory.CreateDirectory(settingsDir);
    builder.Services.AddSingleton(sp => new LocalSettingsService(settingsPath,
        sp.GetRequiredService<ILogger<LocalSettingsService>>()));

    // ── Scanner / telemetry source ────────────────────────────
    string? processName = builder.Configuration.GetValue<string>("BotDs:Scanner:ProcessName");
    int? processId = builder.Configuration.GetValue<int?>("BotDs:Scanner:ProcessId");
    var selector = new ProcessSelector
    {
        ProcessName = processName,
        ProcessId = processId,
    };
    if (!selector.IsValid)
    {
        Log.Warning("No valid process selector configured (BotDs:Scanner:ProcessName or ProcessId). " +
                     "Scanner will remain disconnected until configured.");
    }

    TimeSpan scannerMaxAge = TimeSpan.FromMilliseconds(
        builder.Configuration.GetValue<int>("BotDs:Scanner:LocalMaxAgeMs", 500));

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(selector);
    builder.Services.AddSingleton(sp => new V5ScannerService(
        sp.GetRequiredService<ProcessSelector>(),
        scannerMaxAge,
        timeProvider: sp.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<TelemetryReaderLoop>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TelemetryReaderLoop>());

    WebApplication app = builder.Build();

    app.Use(async (ctx, next) =>
    {
        using IDisposable? _ = LogContext.PushProperty("CorrelationId", ctx.TraceIdentifier);
        await next();
    });

    app.UseSerilogRequestLogging();

    app.UseMiddleware<DashboardSecurityMiddleware>();
    app.UseStaticFiles();
    app.MapDashboardEndpoints();
    app.MapSettingsEndpoints();

    ProfileService profileService = app.Services.GetRequiredService<ProfileService>();
    Directory.CreateDirectory(profileService.DirectoryPath);
    ProfileReloadResult startupReload = await profileService.ReloadAsync();
    if (!startupReload.Success)
        Log.Warning("Startup profile reload failed: {Errors}; continuing with empty cache",
            string.Join("; ", startupReload.Errors));

    string? apiToken = builder.Configuration.GetValue<string>("BotDs:Dashboard:ApiToken");
    string? controlToken = builder.Configuration.GetValue<string>("BotDs:Dashboard:ControlToken");

    if (string.IsNullOrWhiteSpace(apiToken))
        Log.Warning("BotDs:Dashboard:ApiToken is not configured; read API access is disabled");
    if (string.IsNullOrWhiteSpace(controlToken))
        Log.Warning("BotDs:Dashboard:ControlToken is not configured; control operations are disabled");

    if (selector.IsValid)
        Log.Information("Scanner configured: {Selector}", selector.ToString());

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
