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
        api.MapGet("/abilities", GetAbilities);
        api.MapGet("/action-bar", GetActionBar);
        api.MapGet("/profiles", GetProfiles);
        api.MapGet("/profiles/{id}", GetProfileById);
        api.MapPut("/profiles/{id}", SaveProfile);
        api.MapPost("/profiles/reload", ReloadProfiles);
        api.MapPost("/profiles/draft-from-telemetry", DraftProfileFromTelemetry);
        api.MapPost("/control/profile", SetProfile);
        api.MapPost("/control/arm", Arm);
        api.MapPost("/control/disarm", Disarm);
        api.MapPost("/control/emergency-stop", EmergencyStop);
        api.MapPost("/control/clear-stop", ClearStop);
        api.MapGet("/readiness", GetReadiness);
        api.MapPost("/control/output-mode", SetOutputMode);
        api.MapGet("/coordinator", GetCoordinator);
        api.MapGet("/bindings", GetBindings);
        api.MapPost("/control/bindings", SetBindingState);
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
        [FromServices] ActionCoordinator? coordinator = null,
        [FromServices] TelemetryReaderLoop? readerLoop = null)
    {
        return Results.Json(CreateStatusPayload(publisher, stateMachine, profileService, evaluator, coordinator, readerLoop), JsonOptions);
    }

    private static IResult GetCoordinator(
        [FromServices] ActionCoordinator coordinator)
    {
        var history = coordinator.RecentHistory.Select(r => new
        {
            r.Timestamp,
            r.RuleId,
            r.AbilityId,
            r.Key,
            Outcome = r.Outcome.ToString(),
            r.Detail,
        }).ToList();

        BindingVerificationSnapshot bindings = coordinator.Bindings.Snapshot();
        InputSinkStatus inputSink = coordinator.InputSink;
        return Results.Json(new
        {
            OutputMode = coordinator.Mode.ToString(),
            PendingAction = coordinator.PendingAction,
            RecentHistory = history,
            InputSink = new
            {
                inputSink.SupportsLiveInput,
                inputSink.IsReady,
                inputSink.BoundProcessId,
            },
            LiveBlockers = coordinator.GetLiveBlockers(),
            EmergencyHotkey = new
            {
                Binding = coordinator.EmergencyHotkey.Binding,
                Registered = coordinator.EmergencyHotkey.IsRegistered,
                Error = coordinator.EmergencyHotkey.LastError,
            },
            Bindings = new
            {
                bindings.Generation,
                bindings.ProfileId,
                bindings.ProviderSessionId,
                States = bindings.States.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase),
            },
        }, JsonOptions);
    }

    private static IResult GetBindings([FromServices] ActionCoordinator coordinator)
    {
        BindingVerificationSnapshot bindings = coordinator.Bindings.Snapshot();
        return Results.Json(new
        {
            bindings.Generation,
            bindings.ProfileId,
            bindings.ProviderSessionId,
            States = bindings.States.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToString(),
                StringComparer.OrdinalIgnoreCase),
        }, JsonOptions);
    }

    private sealed record SetBindingStateRequest(string? AbilityAlias, string? State);

    private static IResult SetBindingState(
        [FromBody] SetBindingStateRequest request,
        [FromServices] ActionCoordinator coordinator,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log)
    {
        if (string.IsNullOrWhiteSpace(request.AbilityAlias))
            return Results.BadRequest(new { Error = "AbilityAlias is required." });

        if (string.IsNullOrWhiteSpace(request.State)
            || !Enum.TryParse<BindingVerificationState>(request.State, ignoreCase: true, out var state))
        {
            return Results.BadRequest(new { Error = "State must be Unverified, Verified, or Mismatch." });
        }

        // Binding calibration changes require disarmed controller (configuration lease).
        using IDisposable? lease = stateMachine.TryBeginConfiguration();
        if (lease is null)
            return Results.Conflict(new { Error = "Disarm before changing binding verification state." });

        coordinator.Bindings.SetState(request.AbilityAlias, state);
        log.LogInformation("Binding {Alias} set to {State} via dashboard", request.AbilityAlias, state);
        return Results.Ok(new
        {
            AbilityAlias = request.AbilityAlias,
            State = state.ToString(),
            Generation = coordinator.Bindings.Generation,
        });
    }

    private static IResult GetAbilities([FromServices] SnapshotPublisher publisher)
    {
        TelemetryFrame frame = publisher.Latest;
        var abilities = frame.Abilities
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new
            {
                Id = kv.Value.Id,
                kv.Value.Name,
                kv.Value.Available,
                kv.Value.Usable,
                kv.Value.InRange,
                kv.Value.CooldownRemainingMilliseconds,
                kv.Value.CooldownDurationMilliseconds,
                kv.Value.CastTimeMilliseconds,
                kv.Value.IsPassive,
                kv.Value.IsReady,
                NameKnown = !string.Equals(kv.Value.Name, kv.Value.Id, StringComparison.Ordinal),
            })
            .ToList();

        return Results.Json(new
        {
            IsKnown = frame.IsAbilitiesKnown,
            Count = abilities.Count,
            ProviderHealth = frame.Provider.Health.ToString(),
            frame.Provider.Sequence,
            Abilities = abilities,
        }, JsonOptions);
    }

    private static IResult GetActionBar([FromServices] SnapshotPublisher publisher)
    {
        TelemetryFrame frame = publisher.Latest;
        return Results.Json(new
        {
            IsKnown = frame.IsActionBarKnown,
            Page = frame.ActionBarPage,
            Slots = (frame.ActionBarSlots ?? Array.Empty<ActionBarSlotState>())
                .Select(s => new { s.Slot, s.AbilityId, HasAbility = !string.IsNullOrWhiteSpace(s.AbilityId) })
                .ToList(),
            Note = "Keys are not observable. Use slot→abilityId for calibration; bind keys in the profile yourself.",
        }, JsonOptions);
    }

    /// <summary>
    /// Write a disabled draft combat profile from the current live ability inventory.
    /// Ability IDs come only from telemetry; keys stay empty for manual calibration.
    /// Does not invent Warrior combat data.
    /// </summary>
    private static async Task<IResult> DraftProfileFromTelemetry(
        [FromServices] SnapshotPublisher publisher,
        [FromServices] ProfileService profileService,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log,
        CancellationToken ct)
    {
        IDisposable? lease = stateMachine.TryBeginConfiguration();
        if (lease is null)
            return Results.Conflict(new { Error = "Disarm before creating draft profiles." });

        using (lease)
        {
            TelemetryFrame frame = publisher.Latest;
            DraftBuildResult? draft = DraftProfileBuilder.TryBuild(frame, out string? buildError);
            if (draft is null)
                return Results.BadRequest(new { Error = buildError ?? "Draft build failed." });

            Directory.CreateDirectory(profileService.DirectoryPath);
            string filePath = Path.Combine(profileService.DirectoryPath, $"{draft.ProfileId}.json");
            string namesPath = Path.Combine(profileService.DirectoryPath, $"{draft.ProfileId}.names.json");
            string tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, draft.ProfileJson, ct);
            File.Move(tempPath, filePath, overwrite: true);
            await File.WriteAllTextAsync(namesPath, draft.NamesJson, ct);

            ProfileReloadResult reload = await profileService.ReloadAsync(ct);
            if (!reload.Success)
            {
                return Results.BadRequest(new { Error = "Draft saved but reload failed.", reload.Errors });
            }

            log.LogInformation(
                "Draft profile '{ProfileId}' created from live telemetry ({Count} abilities, player={Player})",
                draft.ProfileId, draft.AbilityCount, frame.Player?.Name ?? "?");

            return Results.Ok(new
            {
                Status = "drafted",
                Id = draft.ProfileId,
                Path = filePath,
                NamesPath = namesPath,
                AbilityCount = draft.AbilityCount,
                Character = new { Calling = draft.Calling, Level = draft.Level },
                Note = "Profile is disabled. Keys are empty unless suggested from action-bar slots (1–12 → 1–0,-,=). Confirm bindings before enabling — tool does not invent combat rotations.",
                KeyHintsFromActionBar = draft.KeyHintsFromActionBar,
            });
        }
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

    private static IResult GetProfileById(
        string id,
        [FromServices] ProfileService profileService)
    {
        var profile = profileService.GetProfile(id);
        if (profile is null)
            return Results.NotFound(new { Error = $"Profile '{id}' not found." });

        return Results.Json(profile, JsonOptions);
    }

    private static async Task<IResult> SaveProfile(
        string id,
        [FromBody] JsonElement body,
        [FromServices] ProfileService profileService,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log,
        CancellationToken ct)
    {
        IDisposable? lease = stateMachine.TryBeginConfiguration();
        if (lease is null)
            return Results.Conflict(new { Error = "Disarm before editing profiles." });

        using (lease)
        {
            // Validate before writing — use same options as CombatProfileLoader
            string json = body.GetRawText();
            CombatProfile? profile;
            var validateOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
            };
            try
            {
                using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
                profile = await JsonSerializer.DeserializeAsync<CombatProfile>(ms, validateOptions, ct);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { Error = "Invalid JSON.", Detail = ex.Message });
            }

            var validation = CombatProfileLoader.Validate(profile);
            if (!validation.IsValid)
                return Results.BadRequest(new { Error = "Profile validation failed.", validation.Errors });

            // Id must match URL
            if (!string.Equals(validation.Profile!.Id, id, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { Error = $"Profile id '{validation.Profile.Id}' does not match URL '{id}'." });

            // Atomic write via temp file
            string filePath = Path.Combine(profileService.DirectoryPath, $"{id}.json");
            string tempPath = filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, filePath, overwrite: true);

            // Reload the profile into cache
            ProfileReloadResult reload = await profileService.ReloadAsync(ct);
            if (!reload.Success)
            {
                log.LogWarning("Profile {ProfileId} saved to disk but reload failed: {Errors}",
                    id, string.Join("; ", reload.Errors));
                return Results.BadRequest(new { Error = "Profile saved but reload failed.", reload.Errors });
            }

            log.LogInformation("Profile '{ProfileId}' saved and reloaded via dashboard", id);
            return Results.Ok(new { Status = "saved", Id = id });
        }
    }

    private static IResult Arm(
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ArmingReadinessService readiness,
        [FromServices] ActionCoordinator coordinator,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<Program> log)
    {
        TimeSpan maxAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
        bool requireGameInputReady = coordinator.Mode == OutputMode.Live;
        ReadinessResult ready = readiness.Evaluate(maxAge, requireGameInputReady);
        if (!ready.CanArm)
            return Results.BadRequest(new { Error = "Readiness check failed.", ready.Blockers });

        if (stateMachine.Arm())
        {
            log.LogInformation("Arm requested via dashboard");
            return Results.Ok(new { Status = "armed", ready.Warnings });
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
        [FromServices] ActionCoordinator? coordinator,
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

                    object payload = CreateStatusPayload(publisher, stateMachine, profileService, evaluator, coordinator, readerLoop);

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
        ActionCoordinator? coordinator = null,
        TelemetryReaderLoop? readerLoop = null)
    {
        TelemetryFrame frame = publisher.Latest;
        var (state, stopReason, message, pendingAction) = stateMachine.Snapshot;
        EvaluationResult? lastResult = evaluator.LastResult;

        // Resolve ActionCoordinator from the evaluator's service provider
        // (Alternatively, add ActionCoordinator as a parameter)

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
                frame.Provider.AttachmentProcessId,
            },
            Knownness = new
            {
                Target = frame.TargetKnownness.ToString(),
                frame.IsAbilitiesKnown,
                frame.IsPlayerAurasKnown,
                frame.IsTargetAurasKnown,
                frame.GameInputReady,
            },
            AbilitySummary = new
            {
                IsKnown = frame.IsAbilitiesKnown,
                Count = frame.Abilities.Count,
                ReadyCount = frame.Abilities.Values.Count(a => a.IsReady),
                // Cap for status payload size; full list is GET /api/abilities
                Sample = frame.Abilities.Values
                    .OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(a => new { a.Id, a.Available, a.IsReady, Cd = a.CooldownRemainingMilliseconds })
                    .ToList(),
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
            Coordinator = coordinator is not null
                ? new
                {
                    OutputMode = coordinator.Mode.ToString(),
                    PendingAction = coordinator.PendingAction,
                    InputSink = coordinator.InputSink,
                    LiveBlockers = coordinator.GetLiveBlockers(),
                    HistoryCount = coordinator.RecentHistory.Count,
                    RecentHistory = coordinator.RecentHistory.TakeLast(10).Select(r => new
                    {
                        r.Timestamp,
                        r.RuleId,
                        r.AbilityId,
                        r.Key,
                        Outcome = r.Outcome.ToString(),
                        r.Detail,
                    }).ToList(),
                }
                : null,
        };
    }

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder builder)
    {
        RouteGroupBuilder api = builder.MapGroup("/api/settings");
        api.MapGet("/", GetSettings);
        api.MapPut("/", PutSettings);
        return builder;
    }

    private static IResult GetSettings([FromServices] LocalSettingsService settings)
        => Results.Json(settings.Current, JsonOptions);

    private static async Task<IResult> PutSettings(
        [FromBody] BotDsSettings proposed,
        [FromServices] LocalSettingsService settings,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] ILogger<Program> log,
        CancellationToken ct)
    {
        IDisposable? configurationLease = stateMachine.TryBeginConfiguration();
        if (configurationLease is null)
            return Results.Conflict(new { Error = "Disarm before changing settings." });

        using (configurationLease)
        {
            var errors = await Task.Run(() => settings.TrySave(proposed), ct);
            if (errors is not null)
                return Results.BadRequest(new { Error = "Validation failed", Errors = errors });

            log.LogInformation("Settings updated via dashboard");
            return Results.Ok(settings.Current);
        }
    }

    private sealed record SetProfileRequest(string ProfileId);
    private sealed record SetOutputModeRequest(string? Mode);

    private static IResult GetReadiness(
        [FromServices] ArmingReadinessService readiness,
        [FromServices] ActionCoordinator coordinator,
        [FromServices] IConfiguration configuration)
    {
        TimeSpan maxAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
        bool inputReadyRequired = coordinator.Mode == OutputMode.Live;
        ReadinessResult result = readiness.Evaluate(maxAge, inputReadyRequired);
        return Results.Json(new
        {
            result.CanArm,
            result.Blockers,
            Warnings = result.Warnings.Select(w => w.Message).ToList(),
            InputReadyRequired = inputReadyRequired,
            InputSink = coordinator.InputSink,
            LiveBlockers = coordinator.GetLiveBlockers(),
            ProfileId = result.Profile?.Id,
            PlayerLevel = result.Frame?.Player?.Level,
            PlayerCalling = result.Frame?.Player?.Calling,
            ProviderHealth = result.Frame?.Provider.Health.ToString(),
        }, JsonOptions);
    }

    private static IResult SetOutputMode(
        [FromBody] SetOutputModeRequest request,
        [FromServices] ActionCoordinator coordinator,
        [FromServices] ControllerStateMachine stateMachine,
        [FromServices] IConfiguration configuration,
        [FromServices] ILogger<Program> log)
    {
        if (request.Mode is null)
            return Results.BadRequest(new { Error = "Output mode is required (disabled, dryRun, live)." });

        if (!Enum.TryParse<OutputMode>(request.Mode, ignoreCase: true, out var mode))
            return Results.BadRequest(new { Error = $"Invalid output mode '{request.Mode}'. Valid: disabled, dryRun, live." });

        IDisposable? lease = stateMachine.TryBeginConfiguration();
        if (lease is null && mode != OutputMode.Disabled)
            return Results.Conflict(new { Error = "Disarm before changing output mode." });

        using (lease)
        {
            TimeSpan maxAge = TimeSpan.FromMilliseconds(
                configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
            if (!coordinator.TrySetMode(mode, maxAge))
            {
                return Results.BadRequest(new
                {
                    Error = "Failed to set output mode. Check readiness and Live blockers.",
                    InputSink = coordinator.InputSink,
                    LiveBlockers = coordinator.GetLiveBlockers(),
                });
            }

            log.LogInformation("Output mode set to {Mode} via dashboard", mode);
            return Results.Ok(new { OutputMode = mode.ToString() });
        }
    }
}
