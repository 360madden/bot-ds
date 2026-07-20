using System.Text.Json;
using System.Text.Json.Serialization;
using BotDs.Core;
using BotDs.App.Services;
using Microsoft.AspNetCore.Mvc;

namespace BotDs.App.Endpoints;

public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder builder)
    {
        RouteGroupBuilder api = builder.MapGroup("/api");

        api.MapGet("/status", GetStatus);
        api.MapGet("/profiles", GetProfiles);
        api.MapPost("/profiles/reload", ReloadProfiles);
        api.MapPost("/control/profile", SetProfile);
        api.MapPost("/control/arm", Arm);
        api.MapPost("/control/disarm", Disarm);
        api.MapPost("/control/emergency-stop", EmergencyStop);
        api.MapPost("/control/clear-stop", ClearStop);
        api.MapGet("/events", StreamEvents);

        return builder;
    }

    private static IResult GetSnapshot([FromServices] SnapshotPublisher publisher) =>
        Results.Json(publisher.Latest, JsonOptions);

    private static IResult GetStatus(
        [FromServices] SnapshotPublisher publisher,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ProfileService profileService,
        [FromServices] EvaluatorLoop evaluator,
        [FromServices] TelemetryReaderLoop? readerLoop = null)
    {
        return Results.Json(CreateStatusPayload(publisher, stateMachine, profileService, evaluator, readerLoop), JsonOptions);
    }

    private static IResult GetProfiles([FromServices] ProfileService profileService)
    {
        var profiles = profileService.AvailableProfileIds.Select(id =>
        {
            CombatProfile? p = profileService.GetProfile(id);
            return p is not null
                ? (object)new
                {
                    Id = id,
                    p.Enabled,
                    Character = p.Character,
                    AbilityCount = p.Abilities.Count,
                    RuleCount = p.Rules.Count,
                }
                : new { Id = id };
        }).ToList();

        return Results.Json(new
        {
            ActiveProfileId = profileService.ActiveProfileId,
            Profiles = profiles,
        }, JsonOptions);
    }

    private static IResult Arm(
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ProfileService profileService,
        [FromServices] SnapshotPublisher publisher,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<Program> log)
    {
        if (profileService.ActiveProfile is not { Enabled: true })
            return Results.BadRequest(new { Error = "No enabled active profile selected." });

        TelemetryFrame frame = publisher.Latest;
        TimeSpan maximumAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
        if (!frame.Provider.IsUsable(maximumAge))
            return Results.BadRequest(new { Error = "Telemetry provider is not healthy and fresh." });
        if (frame.Player is not { IsAvailable: true } player || player.Health.IsDead)
            return Results.BadRequest(new { Error = "Live player state is required." });
        if (frame.Target is not { IsAvailable: true, IsHostile: true } target || target.Health.IsDead)
            return Results.BadRequest(new { Error = "A live hostile selected target is required." });

        if (stateMachine.Arm())
        {
            log.LogInformation("Arm requested via dashboard");
            return Results.Ok(new { Status = "armed" });
        }

        return Results.Conflict(new
        {
            Error = "Cannot arm from current state.",
            State = stateMachine.State.ToString(),
        });
    }

    private static IResult Disarm(
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log)
    {
        if (stateMachine.Disarm())
        {
            log.LogInformation("Disarm requested via dashboard");
            return Results.Ok(new { Status = "disarmed" });
        }
        return Results.Conflict(new { Error = "Already disarmed." });
    }

    private static IResult EmergencyStop(
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log)
    {
        stateMachine.EmergencyStop("Dashboard estop");
        log.LogWarning("Emergency stop requested via dashboard");
        return Results.Ok(new { Status = "stopped", Reason = "EmergencyStop" });
    }

    private static IResult ClearStop(
        [FromServices] ControllerStateMachine stateMachine)
    {
        if (stateMachine.ClearStop())
            return Results.Ok(new { Status = "cleared" });
        return Results.Conflict(new { Error = "Controller is not in a stopped state." });
    }

    private static async Task<IResult> ReloadProfiles(
        [FromServices] ProfileService profileService,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log,
        CancellationToken ct)
    {
        IDisposable? configurationLease = stateMachine.TryBeginConfiguration();
        if (configurationLease is null)
            return Results.Conflict(new { Error = "Disarm before reloading profiles." });

        using (configurationLease)
        {
            ProfileReloadResult result = await profileService.ReloadAsync(ct);
            if (!result.Success)
            {
                log.LogWarning("Profile reload failed: {Errors}", string.Join("; ", result.Errors));
                return Results.BadRequest(new
                {
                    Error = "Profile reload failed.",
                    result.Errors,
                });
            }

            log.LogInformation("Profiles reloaded via dashboard");
            return Results.Ok(new { Status = "reloaded", Profiles = profileService.AvailableProfileIds });
        }
    }

    private static IResult SetProfile(
        [FromBody] SetProfileRequest request,
        [FromServices] ProfileService profileService,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileId))
            return Results.BadRequest(new { Error = "ProfileId is required." });

        IDisposable? configurationLease = stateMachine.TryBeginConfiguration();
        if (configurationLease is null)
            return Results.Conflict(new { Error = "Disarm before changing profiles." });

        using (configurationLease)
        {
            if (!profileService.SetActiveProfile(request.ProfileId))
                return Results.BadRequest(new { Error = $"Profile '{request.ProfileId}' not found." });

            log.LogInformation("Profile set to {ProfileId} via dashboard", request.ProfileId);
            return Results.Ok(new { ActiveProfileId = request.ProfileId });
        }
    }

    private static async Task StreamEvents(
        HttpContext context,
        [FromServices] SnapshotPublisher publisher,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] EvaluatorLoop evaluator,
        [FromServices] ProfileService profileService,
        [FromServices] TelemetryReaderLoop? readerLoop,
        [FromServices] ILogger<Program> log,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        ulong lastSequence = 0;
        string lastState = string.Empty;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                TelemetryFrame frame = publisher.Latest;
                string currentState = stateMachine.State.ToString();

                if (frame.Provider.Sequence != lastSequence || currentState != lastState)
                {
                    lastSequence = frame.Provider.Sequence;
                    lastState = currentState;

                    object payload = CreateStatusPayload(publisher, stateMachine, profileService, evaluator, readerLoop);

                    await context.Response.WriteAsync("data: ", ct);
                    await JsonSerializer.SerializeAsync(context.Response.Body, payload, JsonOptions, ct);
                    await context.Response.WriteAsync("\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                }

                await Task.Delay(100, ct);
            }
        }
        catch (OperationCanceledException)
        {
            log.LogDebug("SSE connection closed");
        }
    }

    private static object CreateStatusPayload(
        SnapshotPublisher publisher,
        ControllerStateMachine stateMachine,
        ProfileService profileService,
        EvaluatorLoop evaluator,
        TelemetryReaderLoop? readerLoop = null)
    {
        TelemetryFrame frame = publisher.Latest;
        var (state, stopReason, message, pendingAction) = stateMachine.Snapshot;
        EvaluationResult? lastResult = evaluator.LastResult;

        return new
        {
            Timestamp = DateTimeOffset.UtcNow,
            Provider = new
            {
                Health = frame.Provider.Health.ToString(),
                frame.Provider.ProtocolVersion,
                frame.Provider.SessionId,
                frame.Provider.Sequence,
                frame.Provider.ProducerFrameMilliseconds,
                frame.Provider.ReceivedAtUtc,
                AgeMilliseconds = frame.Provider.Age == TimeSpan.MaxValue
                    ? (double?)null
                    : frame.Provider.Age.TotalMilliseconds,
                frame.Provider.ClientVersion,
                frame.Provider.Fault,
                frame.Provider.IsTruncated,
            },
            Scanner = readerLoop is not null
                ? new
                {
                    IsAttached = readerLoop.AttachmentPid > 0,
                    readerLoop.AttachmentPid,
                    readerLoop.AttachmentGeneration,
                    Metrics = new
                    {
                        readerLoop.Metrics.FullScanCount,
                        readerLoop.Metrics.CacheHitCount,
                        readerLoop.Metrics.CacheMissCount,
                        readerLoop.Metrics.SmallWindowHits,
                        readerLoop.Metrics.SmallWindowMisses,
                        readerLoop.Metrics.ReadFailures,
                        readerLoop.Metrics.ReadCycleFailures,
                        readerLoop.Metrics.CandidateLimitHits,
                        readerLoop.Metrics.AttachmentCount,
                        readerLoop.Metrics.BytesScanned,
                        readerLoop.Metrics.ValidCandidatesFound,
                        readerLoop.Metrics.LastScanUtc,
                        readerLoop.Metrics.LastReadCycleUtc,
                    },
                    LastResultHealth = readerLoop.LastResult?.ReadResult.TransportHealth.ToString(),
                }
                : null,
            Controller = new
            {
                State = state.ToString(),
                StopReason = stopReason.ToString(),
                Message = message,
                PendingDecision = pendingAction,
                LastEvaluation = lastResult is not null
                    ? new
                    {
                        State = lastResult.State.ToString(),
                        lastResult.HasAction,
                        StopReason = lastResult.StopReason.ToString(),
                        lastResult.Message,
                        RejectionCount = lastResult.Rejections.Count,
                        lastResult.Action,
                        lastResult.Rejections,
                    }
                    : null,
            },
            frame.Player,
            frame.Target,
            ActiveProfileId = profileService.ActiveProfileId,
        };
    }

    private sealed record SetProfileRequest(string ProfileId);
}
