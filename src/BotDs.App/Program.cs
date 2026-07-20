using BotDs.App.Endpoints;
using BotDs.App.Services;
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
            retainedFileCountLimit: 90,
            flushToDiskInterval: TimeSpan.FromSeconds(1)));

    builder.Services.AddSingleton<SnapshotPublisher>();
    builder.Services.AddSingleton<ProfileService>();
    builder.Services.AddSingleton<ControllerStateMachine>();
    builder.Services.AddSingleton<EvaluatorLoop>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EvaluatorLoop>());

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

    ProfileService profileService = app.Services.GetRequiredService<ProfileService>();
    Directory.CreateDirectory(profileService.DirectoryPath);
    ProfileReloadResult startupReload = await profileService.ReloadAsync();
    if (!startupReload.Success)
        Log.Warning("Startup profile reload failed: {Errors}; continuing with empty cache",
            string.Join("; ", startupReload.Errors));

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
