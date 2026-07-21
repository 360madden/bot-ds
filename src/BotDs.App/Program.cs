using BotDs.App.Endpoints;
using BotDs.App.Services;
using BotDs.Core;
using BotDs.Input;
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
    builder.Services.AddSingleton<ITelemetrySource, SnapshotTelemetrySource>();
    builder.Services.AddSingleton<ProfileService>();
    builder.Services.AddSingleton<ControllerStateMachine>();
    builder.Services.AddSingleton<BindingVerificationTracker>();
    builder.Services.AddSingleton<EvaluatorLoop>();
    builder.Services.AddSingleton<ArmingReadinessService>();
    // ── Key sink: fake (dry-run) or Windows (live) ──────────
    bool useWindowsKeySink = builder.Configuration.GetValue<bool>("BotDs:Input:UseWindowsKeySink", false);
    if (useWindowsKeySink)
    {
        int? boundPid = builder.Configuration.GetValue<int?>("BotDs:Input:BoundPid");
        if (boundPid is > 0)
        {
            int chordMs = builder.Configuration.GetValue<int>("BotDs:Input:ChordPressMs", 30);
            builder.Services.AddSingleton<IKeySink>(sp =>
                new WindowsKeySink(boundPid.Value, chordPressMs: chordMs,
                    logger: sp.GetRequiredService<ILogger<WindowsKeySink>>()));
            Log.Information("WindowsKeySink bound to PID {Pid}, chord press {ChordMs}ms", boundPid, chordMs);
        }
        else
        {
            Log.Warning("BotDs:Input:UseWindowsKeySink=true but BotDs:Input:BoundPid not set; falling back to FakeKeySink");
            builder.Services.AddSingleton<IKeySink>(new FakeKeySink());
        }
    }
    else
    {
        builder.Services.AddSingleton<IKeySink>(new FakeKeySink());
    }

    // ── Emergency hotkey (M8) ────────────────────────────────
    string emergencyHotkey = builder.Configuration.GetValue<string>("BotDs:Action:EmergencyHotkey")
        ?? "Ctrl+Shift+F12";
    bool useWindowsEmergencyHotkey = builder.Configuration.GetValue(
        "BotDs:Action:UseWindowsEmergencyHotkey", true);
    if (useWindowsEmergencyHotkey)
    {
        builder.Services.AddSingleton<IEmergencyHotkey>(new WindowsEmergencyHotkey(emergencyHotkey));
        Log.Information("Windows emergency hotkey configured: {Binding}", emergencyHotkey);
    }
    else
    {
        builder.Services.AddSingleton<IEmergencyHotkey>(new FakeEmergencyHotkey(emergencyHotkey));
        Log.Information("Fake emergency hotkey configured: {Binding}", emergencyHotkey);
    }

    builder.Services.AddSingleton<ActionCoordinator>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EvaluatorLoop>());
    builder.Services.AddHostedService<EmergencyHotkeyHostedService>();

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
    app.MapFallbackToFile("index.html");

    ProfileService profileService = app.Services.GetRequiredService<ProfileService>();
    Directory.CreateDirectory(profileService.DirectoryPath);
    ProfileReloadResult startupReload = await profileService.ReloadAsync();
    if (!startupReload.Success)
        Log.Warning("Startup profile reload failed: {Errors}; continuing with empty cache",
            string.Join("; ", startupReload.Errors));

    Log.Information("Dashboard API is loopback-only (no token auth)");

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
