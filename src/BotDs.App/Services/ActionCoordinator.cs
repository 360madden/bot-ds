using BotDs.Core;
using BotDs.Input;

namespace BotDs.App.Services;

/// <summary>
/// Output mode for the action coordinator.
/// </summary>
public enum OutputMode
{
    /// <summary>No actions evaluated or dispatched.</summary>
    Disabled,

    /// <summary>Evaluate and validate actions but do not send input.</summary>
    DryRun,

    /// <summary>Evaluate, validate, and dispatch actions via input sink. (M7+)</summary>
    Live,
}

/// <summary>
/// Tracks the outcome of a single action dispatch attempt.
/// </summary>
public sealed record DispatchRecord(
    DateTimeOffset Timestamp,
    string RuleId,
    string AbilityId,
    string Key,
    DispatchOutcome Outcome,
    string? Detail = null);

public sealed record InputSinkStatus(
    bool SupportsLiveInput,
    bool IsReady,
    int BoundProcessId);

public enum DispatchOutcome
{
    /// <summary>Action dispatched successfully (dry-run: logged).</summary>
    Dispatched,

    /// <summary>Blocked by rate limit.</summary>
    RateLimited,

    /// <summary>Blocked — pending action not yet acknowledged.</summary>
    PendingActionBlocked,

    /// <summary>Blocked — pre-dispatch revalidation failed.</summary>
    RevalidationFailed,

    /// <summary>Action timed out waiting for acknowledgement.</summary>
    AcknowledgementTimeout,

    /// <summary>Typed acknowledgement matched post-dispatch telemetry.</summary>
    Acknowledged,

    /// <summary>Pending action discarded due to session/target/source change.</summary>
    PendingInvalidated,

    /// <summary>Cancelled by controller state change.</summary>
    Cancelled,

    /// <summary>Coordinator is not armed.</summary>
    NotArmed,
}

/// <summary>
/// Serialized action coordinator. Consumes evaluation results while armed,
/// enforces rate limits, validates preconditions, tracks pending actions with
/// typed acknowledgement matching, and records binding verification on success.
/// </summary>
public sealed class ActionCoordinator
{
    private readonly TimeSpan _ackTimeout;
    private readonly TimeSpan _minGlobalInterval;
    private readonly TimeSpan _minPerKeyInterval;
    private readonly bool _requireBindingVerificationForLive;
    private readonly bool _requireEmergencyHotkeyForLive;
    private readonly bool _detectExternalActionConflicts;
    private readonly string _emergencyHotkeyBinding;
    private TimeSpan _maximumTelemetryAge;
    private readonly SnapshotPublisher _publisher;
    private readonly ControllerStateMachine _stateMachine;
    private readonly ProfileService _profiles;
    private readonly ArmingReadinessService _readiness;
    private readonly BindingVerificationTracker _bindings;
    private readonly IEmergencyHotkey _emergencyHotkey;
    private readonly IKeySink _keySink;
    private readonly TimeProvider _time;
    private readonly ILogger<ActionCoordinator> _log;

    private readonly object _lock = new();
    private OutputMode _outputMode = OutputMode.Disabled;

    private DateTimeOffset _lastDispatchUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _perKeyLastDispatch = new(StringComparer.OrdinalIgnoreCase);

    private ActionDecision? _pendingAction;
    private PendingActionBaseline? _pendingBaseline;
    private DateTimeOffset _pendingActionDispatchedUtc = DateTimeOffset.MinValue;
    private TelemetryFrame? _previousFrame;

    private readonly List<DispatchRecord> _recentHistory = [];
    private const int MaxHistory = 200;

    private readonly SemaphoreSlim _dispatchFence = new(1, 1);

    public ActionCoordinator(
        SnapshotPublisher publisher,
        ControllerStateMachine stateMachine,
        ProfileService profiles,
        ArmingReadinessService readiness,
        IConfiguration configuration,
        IKeySink? keySink = null,
        TimeProvider? timeProvider = null,
        ILogger<ActionCoordinator>? log = null,
        BindingVerificationTracker? bindingVerification = null,
        IEmergencyHotkey? emergencyHotkey = null)
    {
        _publisher = publisher;
        _stateMachine = stateMachine;
        _profiles = profiles;
        _readiness = readiness;
        _bindings = bindingVerification ?? new BindingVerificationTracker();
        _emergencyHotkeyBinding = configuration.GetValue<string>("BotDs:Action:EmergencyHotkey")
            ?? "Ctrl+Shift+F12";
        // Default fake hotkey is pre-registered so unit tests and DryRun hosts
        // are not blocked; production hosts inject WindowsEmergencyHotkey via DI.
        _emergencyHotkey = emergencyHotkey ?? CreateDefaultFakeHotkey(_emergencyHotkeyBinding);
        _keySink = keySink ?? new FakeKeySink();
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLoggerFactory.Instance.CreateLogger<ActionCoordinator>();

        _ackTimeout = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Action:AcknowledgementTimeoutMs", 2000));
        _minGlobalInterval = TimeSpan.FromMilliseconds(
            (int)(1000.0 / configuration.GetValue<int>("BotDs:Action:MaxGlobalPerSecond", 4)));
        _minPerKeyInterval = TimeSpan.FromMilliseconds(
            (int)(1000.0 / configuration.GetValue<int>("BotDs:Action:MaxPerKeyPerSecond", 2)));
        _requireBindingVerificationForLive = configuration.GetValue(
            "BotDs:Action:RequireBindingVerificationForLive", true);
        _requireEmergencyHotkeyForLive = configuration.GetValue(
            "BotDs:Action:RequireEmergencyHotkeyForLive", true);
        _detectExternalActionConflicts = configuration.GetValue(
            "BotDs:Action:DetectExternalActionConflicts", true);
        _maximumTelemetryAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
    }

    public OutputMode Mode
    {
        get { lock (_lock) return _outputMode; }
    }

    public ActionDecision? PendingAction
    {
        get { lock (_lock) return _pendingAction; }
    }

    public IReadOnlyList<DispatchRecord> RecentHistory
    {
        get { lock (_lock) return _recentHistory.ToList(); }
    }

    public BindingVerificationTracker Bindings => _bindings;

    public IEmergencyHotkey EmergencyHotkey => _emergencyHotkey;

    public InputSinkStatus InputSink => new(
        _keySink.SupportsLiveInput,
        _keySink.IsReady,
        _keySink.BoundPid);

    public IReadOnlyList<string> GetLiveBlockers()
    {
        lock (_lock)
        {
            return BuildLiveModeBlockersLocked(_profiles.ActiveProfile, _publisher.Latest);
        }
    }

    /// <summary>
    /// Attempt to set output mode. Non-Disabled modes require readiness.
    /// Live additionally requires verified bindings and a registered emergency hotkey
    /// when configured.
    /// </summary>
    public bool TrySetMode(OutputMode mode, TimeSpan maxTelemetryAge)
    {
        lock (_lock)
        {
            if (mode == OutputMode.Disabled)
            {
                CancelPendingLocked("Output disabled");
                _outputMode = OutputMode.Disabled;
                _perKeyLastDispatch.Clear();
                _previousFrame = null;
                _log.LogInformation("Output mode set to Disabled");
                return true;
            }

            if (_outputMode != OutputMode.Disabled)
            {
                _log.LogWarning("Cannot change output mode from {Current} to {Requested} — set Disabled first",
                    _outputMode, mode);
                return false;
            }

            ReadinessResult ready = _readiness.Evaluate(
                maxTelemetryAge,
                requireGameInputReady: mode == OutputMode.Live);
            if (!ready.CanArm)
            {
                _log.LogWarning("Readiness check failed for mode {Mode}: {Blockers}",
                    mode, string.Join("; ", ready.Blockers));
                return false;
            }

            if (mode == OutputMode.Live)
            {
                AlignBindingsLocked(ready.Profile, ready.Frame);
                IReadOnlyList<string> liveBlockers = BuildLiveModeBlockersLocked(ready.Profile, ready.Frame);
                if (liveBlockers.Count > 0)
                {
                    _log.LogWarning("Live mode blocked: {Blockers}", string.Join("; ", liveBlockers));
                    return false;
                }
            }

            _maximumTelemetryAge = maxTelemetryAge;
            _outputMode = mode;
            _previousFrame = mode == OutputMode.Live ? ready.Frame : null;
            CancelPendingLocked("Output mode changed");
            _log.LogInformation("Output mode set to {Mode}", mode);
            return true;
        }
    }

    public bool Disable()
    {
        lock (_lock)
        {
            CancelPendingLocked("Output disabled");
            _outputMode = OutputMode.Disabled;
            _perKeyLastDispatch.Clear();
            _previousFrame = null;
            _log.LogInformation("Output mode set to Disabled");
            return true;
        }
    }

    /// <summary>
    /// Armed-tick housekeeping: external-action conflict detection, then pending acknowledgement.
    /// Must be called every armed evaluation tick, including when no new action is produced.
    /// </summary>
    public DispatchRecord? ObservePending(TelemetryFrame frame, long controllerGeneration)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_lock)
        {
            if (_outputMode != OutputMode.Live)
            {
                _previousFrame = null;
                return null;
            }

            AlignBindingsLocked(_profiles.ActiveProfile, frame);

            var state = _stateMachine.State;
            if (state is not (ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
                && _detectExternalActionConflicts
                && _previousFrame is not null
                && _profiles.ActiveProfile is not null
                && ExternalActionConflictDetector.TryDetect(
                    _profiles.ActiveProfile, _previousFrame, frame, _pendingAction, out string conflictDetail))
            {
                DispatchRecord record = RecordAndReturnLocked(
                    _pendingAction ?? new ActionDecision("external", "external", "external", "?", AcknowledgementKind.Cooldown, frame.Provider.Sequence),
                    DispatchOutcome.Cancelled,
                    conflictDetail);
                CancelPendingLocked(conflictDetail);
                _stateMachine.Stop(StopReason.ExternalActionConflict, conflictDetail);
                DisableUnlocked();
                _previousFrame = frame;
                _log.LogWarning("{Detail}", conflictDetail);
                return record;
            }

            _previousFrame = frame;

            if (_pendingAction is null || _pendingBaseline is null)
                return null;

            if (_stateMachine.Generation != controllerGeneration)
            {
                return InvalidatePendingLocked("Controller generation changed");
            }

            state = _stateMachine.State;
            if (state is ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
            {
                return InvalidatePendingLocked($"Controller state is {state}");
            }

            AcknowledgementMatch match = ActionAcknowledgementMatcher.TryMatch(_pendingBaseline, frame);
            if (match == AcknowledgementMatch.Matched)
            {
                ActionDecision action = _pendingAction;
                if (IsSupportedLiveAcknowledgement(action.Acknowledgement))
                    _bindings.MarkVerified(action.AbilityAlias);
                DispatchRecord record = RecordAndReturnLocked(
                    action, DispatchOutcome.Acknowledged,
                    $"ack={action.Acknowledgement}; seq={frame.Provider.Sequence}");
                CancelPendingLocked("Acknowledged");
                _log.LogInformation(
                    "Action acknowledged: {AbilityId} via {Kind} (binding {Alias} Verified)",
                    action.AbilityId, action.Acknowledgement, action.AbilityAlias);
                return record;
            }

            if (match == AcknowledgementMatch.Invalidated)
            {
                return InvalidatePendingLocked("Session, source generation, or target changed");
            }

            // Still pending — check timeout
            DateTimeOffset now = _time.GetUtcNow();
            TimeSpan elapsed = now - _pendingActionDispatchedUtc;
            if (elapsed > _ackTimeout)
            {
                ActionDecision timedOut = _pendingAction;
                DispatchRecord record = RecordAndReturnLocked(
                    timedOut, DispatchOutcome.AcknowledgementTimeout,
                    $"Pending action timed out after {elapsed.TotalMilliseconds:F0}ms");
                CancelPendingLocked("Acknowledgement timeout");

                if (_outputMode == OutputMode.Live)
                {
                    _stateMachine.Stop(
                        StopReason.ActionNotAcknowledged,
                        $"Action '{timedOut.AbilityId}' was not acknowledged within {_ackTimeout.TotalMilliseconds:F0}ms.");
                    DisableUnlocked();
                }

                return record;
            }

            return null;
        }
    }

    /// <summary>
    /// Consume an evaluation result. Called by EvaluatorLoop on each armed tick that produces an action.
    /// Returns a dispatch record if a dispatch was attempted, null otherwise.
    /// </summary>
    public DispatchRecord? Consume(EvaluationResult result, long controllerGeneration)
    {
        OutputMode mode;
        lock (_lock) { mode = _outputMode; }
        if (mode == OutputMode.Disabled)
            return null;

        // Always attempt pending observation first using latest publisher frame.
        DispatchRecord? observed = ObservePending(_publisher.Latest, controllerGeneration);
        if (observed is not null
            && observed.Outcome is DispatchOutcome.AcknowledgementTimeout
                or DispatchOutcome.PendingInvalidated)
        {
            // Fail-closed paths should not immediately re-dispatch in the same tick.
            if (observed.Outcome == DispatchOutcome.AcknowledgementTimeout && mode == OutputMode.Live)
                return observed;
        }

        if (!result.HasAction || result.Action is null)
            return observed;

        // If we just acknowledged, fall through and allow a new dispatch this tick.
        return TryDispatch(result.Action, controllerGeneration) ?? observed;
    }

    private DispatchRecord? TryDispatch(ActionDecision action, long controllerGeneration)
    {
        var now = _time.GetUtcNow();

        if (!_dispatchFence.Wait(0))
        {
            _log.LogDebug("Dispatch fence busy — skipping cycle");
            return null;
        }

        try
        {
            lock (_lock)
            {
                if (_outputMode == OutputMode.Disabled)
                    return RecordAndReturnLocked(action, DispatchOutcome.NotArmed, "Output disabled");

                if (_outputMode == OutputMode.Live && _pendingAction is not null)
                {
                    TimeSpan elapsed = now - _pendingActionDispatchedUtc;
                    if (elapsed > _ackTimeout)
                    {
                        var timeout = RecordAndReturnLocked(
                            _pendingAction, DispatchOutcome.AcknowledgementTimeout,
                            $"Pending action timed out after {elapsed.TotalMilliseconds:F0}ms");
                        CancelPendingLocked("Acknowledgement timeout");
                        if (_outputMode == OutputMode.Live)
                        {
                            _stateMachine.Stop(
                                StopReason.ActionNotAcknowledged,
                                $"Action '{timeout.AbilityId}' was not acknowledged within {_ackTimeout.TotalMilliseconds:F0}ms.");
                            DisableUnlocked();
                            return timeout;
                        }
                    }
                    else
                    {
                        return RecordAndReturnLocked(
                            action, DispatchOutcome.PendingActionBlocked,
                            $"Pending action awaiting acknowledgement ({elapsed.TotalMilliseconds:F0}ms of {_ackTimeout.TotalMilliseconds:F0}ms)");
                    }
                }

                TimeSpan globalElapsed = now - _lastDispatchUtc;
                if (globalElapsed < _minGlobalInterval)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.RateLimited,
                        $"Global rate limit ({_minGlobalInterval.TotalMilliseconds:F0}ms min), elapsed {globalElapsed.TotalMilliseconds:F0}ms");
                }

                if (_perKeyLastDispatch.TryGetValue(action.Key, out var lastKeyUtc))
                {
                    TimeSpan keyElapsed = now - lastKeyUtc;
                    if (keyElapsed < _minPerKeyInterval)
                    {
                        return RecordAndReturnLocked(
                            action, DispatchOutcome.RateLimited,
                            $"Per-key rate limit for '{action.Key}' ({_minPerKeyInterval.TotalMilliseconds:F0}ms min), elapsed {keyElapsed.TotalMilliseconds:F0}ms");
                    }
                }

                if (_stateMachine.Generation != controllerGeneration)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.Cancelled,
                        "Controller generation changed — evaluation is stale");
                }

                var state = _stateMachine.State;
                if (state is ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.Cancelled,
                        $"Controller state is {state}");
                }

                if (_outputMode == OutputMode.DryRun)
                {
                    _lastDispatchUtc = now;
                    _perKeyLastDispatch[action.Key] = now;
                    _log.LogInformation(
                        "[DRY-RUN] Action: Rule={RuleId}, Ability={AbilityId}, Key={Key}, Ack={Ack}, Seq={Seq}",
                        action.RuleId, action.AbilityId, action.Key, action.Acknowledgement, action.FrameSequence);
                    return RecordAndReturnLocked(action, DispatchOutcome.Dispatched, "dry-run");
                }

                TelemetryFrame frame = _publisher.Latest;
                if (!TryValidateLiveDispatchLocked(
                    action,
                    frame,
                    out ActionDecision? currentAction,
                    out string validationDetail,
                    out StopReason fatalStopReason))
                {
                    if (fatalStopReason != StopReason.None)
                    {
                        _stateMachine.Stop(fatalStopReason, validationDetail);
                        DisableUnlocked();
                    }

                    return RecordAndReturnLocked(action, DispatchOutcome.RevalidationFailed, validationDetail);
                }

                bool dispatched = _keySink.DispatchKey(currentAction!.Key, CancellationToken.None);
                if (!dispatched)
                {
                    string detail = $"Key sink rejected dispatch for '{currentAction.Key}'";
                    _keySink.LatchFault(detail);
                    _stateMachine.Stop(StopReason.IntegrityFailure, detail);
                    DisableUnlocked();
                    return RecordAndReturnLocked(currentAction, DispatchOutcome.RevalidationFailed,
                        $"{detail}; controller stopped and output disabled");
                }
                _log.LogInformation(
                    "[LIVE] Key dispatched: {Key} for {AbilityId}",
                    currentAction.Key, currentAction.AbilityId);

                _lastDispatchUtc = now;
                _perKeyLastDispatch[currentAction.Key] = now;
                _pendingAction = currentAction;
                _pendingActionDispatchedUtc = now;
                _pendingBaseline = ActionAcknowledgementMatcher.CaptureBaseline(currentAction, frame, now);

                return RecordAndReturnLocked(
                    currentAction, DispatchOutcome.Dispatched, "live");
            }
        }
        finally
        {
            _dispatchFence.Release();
        }
    }

    public void CancelPending(string reason)
    {
        lock (_lock) CancelPendingLocked(reason);
    }

    private void AlignBindingsLocked(CombatProfile? profile, TelemetryFrame? frame)
    {
        _bindings.Align(profile?.Id, frame?.Provider.SessionId);
    }

    private DispatchRecord InvalidatePendingLocked(string reason)
    {
        ActionDecision? action = _pendingAction;
        if (action is null)
            return RecordAndReturnLocked(
                new ActionDecision("?", "?", "?", "?", AcknowledgementKind.Cooldown, 0),
                DispatchOutcome.PendingInvalidated, reason);

        DispatchRecord record = RecordAndReturnLocked(action, DispatchOutcome.PendingInvalidated, reason);
        CancelPendingLocked(reason);
        return record;
    }

    private void CancelPendingLocked(string reason)
    {
        if (_pendingAction is not null)
        {
            _log.LogInformation("Cancelled pending action {AbilityId}: {Reason}",
                _pendingAction.AbilityId, reason);
        }
        _pendingAction = null;
        _pendingBaseline = null;
        _pendingActionDispatchedUtc = DateTimeOffset.MinValue;
    }

    private void DisableUnlocked()
    {
        CancelPendingLocked("Output forced disabled");
        _outputMode = OutputMode.Disabled;
        _perKeyLastDispatch.Clear();
        _previousFrame = null;
        _log.LogInformation("Output mode forced to Disabled");
    }

    private bool TryValidateLiveDispatchLocked(
        ActionDecision action,
        TelemetryFrame frame,
        out ActionDecision? currentAction,
        out string detail,
        out StopReason fatalStopReason)
    {
        currentAction = null;
        fatalStopReason = StopReason.None;

        if (_outputMode != OutputMode.Live)
        {
            detail = "Output mode changed before dispatch";
            return false;
        }

        ProviderStatus provider = frame.Provider;
        if (!provider.IsUsable(_maximumTelemetryAge, _time.GetUtcNow()))
        {
            detail = $"Latest telemetry is not healthy and fresh (health={provider.Health}, age={provider.Age.TotalMilliseconds:F0}ms)";
            fatalStopReason = StopReason.TelemetryStale;
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.ProviderSessionId)
            || string.IsNullOrWhiteSpace(provider.SessionId)
            || !string.Equals(action.ProviderSessionId, provider.SessionId, StringComparison.Ordinal))
        {
            detail = "Provider session identity changed before dispatch";
            fatalStopReason = StopReason.IntegrityFailure;
            return false;
        }

        if (action.SourceGeneration != provider.SourceGeneration)
        {
            detail = "Telemetry source generation changed before dispatch";
            fatalStopReason = StopReason.IntegrityFailure;
            return false;
        }

        if (action.AttachmentProcessId is not > 0
            || provider.AttachmentProcessId is not > 0
            || action.AttachmentProcessId != provider.AttachmentProcessId)
        {
            detail = "RIFT attachment process identity changed before dispatch";
            fatalStopReason = StopReason.ProcessExited;
            return false;
        }

        if (!IsSameOrNewerSequence(provider.Sequence, action.FrameSequence))
        {
            detail = $"Latest telemetry sequence {provider.Sequence} is older or ambiguously ordered relative to decision sequence {action.FrameSequence}";
            fatalStopReason = StopReason.SequenceDiscontinuity;
            return false;
        }

        if (!_keySink.SupportsLiveInput || !_keySink.IsReady || _keySink.BoundPid <= 0
            || _keySink.BoundPid != provider.AttachmentProcessId)
        {
            detail = $"Live input sink is unavailable or bound to the wrong process (supportsLive={_keySink.SupportsLiveInput}, ready={_keySink.IsReady}, boundPid={_keySink.BoundPid}, telemetryPid={provider.AttachmentProcessId?.ToString() ?? "unknown"})";
            fatalStopReason = StopReason.IntegrityFailure;
            return false;
        }

        if (frame.GameInputReady != true)
        {
            detail = frame.GameInputReady is null
                ? "Game input readiness became unknown before dispatch"
                : "Game input became blocked before dispatch";
            return false;
        }

        UnitState? player = frame.Player;
        if (player is null || !player.IsAvailable || player.Health.IsDead)
        {
            detail = "Player is unavailable or dead in the latest telemetry";
            return false;
        }

        UnitState? target = frame.Target;
        if (target is null || !target.IsAvailable || target.Health.IsDead || !target.IsHostile)
        {
            detail = "Target is unavailable, dead, or non-hostile in the latest telemetry";
            return false;
        }

        if (string.IsNullOrWhiteSpace(action.TargetId)
            || !string.Equals(action.TargetId, target.Id, StringComparison.Ordinal))
        {
            detail = "Selected target changed before dispatch";
            return false;
        }

        if (!IsSupportedLiveAcknowledgement(action.Acknowledgement))
        {
            detail = $"Acknowledgement kind '{action.Acknowledgement}' is unsupported in Live mode";
            fatalStopReason = StopReason.IntegrityFailure;
            return false;
        }

        if (!frame.IsAbilitiesKnown
            || !frame.Abilities.TryGetValue(action.AbilityId, out AbilityState? ability)
            || ability is null
            || !ability.Available
            || !ability.IsReady
            || ability.Usable != true
            || ability.InRange != true)
        {
            detail = $"Ability '{action.AbilityId}' is no longer known ready, usable, and in range";
            return false;
        }

        CombatProfile? profile = _profiles.ActiveProfile;
        if (profile is null || !profile.Enabled)
        {
            detail = "Active combat profile is unavailable or disabled";
            fatalStopReason = StopReason.ProfileMismatch;
            return false;
        }

        if (_requireBindingVerificationForLive)
        {
            IReadOnlyList<string> bindingBlockers = _bindings.GetLiveBlockers(profile, player.Level);
            if (bindingBlockers.Count > 0)
            {
                detail = $"Binding verification changed before dispatch: {string.Join("; ", bindingBlockers)}";
                fatalStopReason = StopReason.IntegrityFailure;
                return false;
            }
        }

        var evaluator = new CombatEvaluator(_maximumTelemetryAge, _time);
        EvaluationResult current = evaluator.Evaluate(profile, frame);
        if (current.Action is null)
        {
            detail = current.Message ?? "No combat action currently wins evaluation";
            return false;
        }

        currentAction = current.Action;
        if (!SameAction(action, currentAction))
        {
            detail = "The current winning combat decision changed before dispatch";
            currentAction = null;
            return false;
        }

        detail = "Live dispatch fence passed";
        return true;
    }

    private IReadOnlyList<string> BuildLiveModeBlockersLocked(CombatProfile? profile, TelemetryFrame? frame)
    {
        var blockers = new List<string>();

        if (!_keySink.SupportsLiveInput)
            blockers.Add("Configured input sink does not support Live input.");
        if (!_keySink.IsReady)
            blockers.Add("Configured input sink is not ready.");
        if (_keySink.BoundPid <= 0)
            blockers.Add("Configured input sink has no positive bound process id.");

        int? attachmentPid = frame?.Provider.AttachmentProcessId;
        if (attachmentPid is not > 0)
            blockers.Add("Telemetry does not identify the attached RIFT process.");
        else if (_keySink.BoundPid > 0 && _keySink.BoundPid != attachmentPid)
            blockers.Add($"Input sink PID {_keySink.BoundPid} does not match telemetry attachment PID {attachmentPid}.");

        if (frame?.GameInputReady is null)
            blockers.Add("Game input readiness is unknown.");
        else if (frame.GameInputReady is false)
            blockers.Add("Game input is currently blocked.");

        if (profile is not null)
        {
            if (ProfileCollidesWithEmergencyHotkey(profile, _emergencyHotkey.Binding))
                blockers.Add($"A profile binding collides with emergency hotkey '{_emergencyHotkey.Binding}'.");

            foreach (CombatRule rule in profile.Rules.Where(r => r is { Enabled: true }))
            {
                if (!IsSupportedLiveAcknowledgement(rule.Acknowledgement))
                    blockers.Add($"Rule '{rule.Id}' uses unsupported Live acknowledgement '{rule.Acknowledgement}'.");
            }

            if (_requireBindingVerificationForLive)
                blockers.AddRange(_bindings.GetLiveBlockers(profile, frame?.Player?.Level));
        }

        if (_requireEmergencyHotkeyForLive && !_emergencyHotkey.IsRegistered)
        {
            blockers.Add($"Emergency hotkey '{_emergencyHotkey.Binding}' is not registered ({_emergencyHotkey.LastError ?? "not registered"}).");
        }

        return blockers;
    }

    private static bool IsSupportedLiveAcknowledgement(AcknowledgementKind acknowledgement) =>
        acknowledgement is AcknowledgementKind.Cast or AcknowledgementKind.Cooldown;

    private static bool SameAction(ActionDecision expected, ActionDecision current) =>
        string.Equals(expected.RuleId, current.RuleId, StringComparison.Ordinal)
        && string.Equals(expected.AbilityAlias, current.AbilityAlias, StringComparison.Ordinal)
        && string.Equals(expected.AbilityId, current.AbilityId, StringComparison.Ordinal)
        && string.Equals(expected.Key, current.Key, StringComparison.OrdinalIgnoreCase)
        && expected.Acknowledgement == current.Acknowledgement;

    private static bool IsSameOrNewerSequence(ulong current, ulong baseline)
    {
        if (current > uint.MaxValue || baseline > uint.MaxValue)
            return current >= baseline;

        uint difference = unchecked((uint)current - (uint)baseline);
        return difference == 0 || difference < 0x80000000u;
    }

    private static bool ProfileCollidesWithEmergencyHotkey(CombatProfile profile, string emergencyHotkey)
    {
        foreach (AbilityBinding binding in profile.Abilities.Values)
        {
            if (binding is not { Enabled: true })
                continue;
            if (VirtualKeyMap.Collides(binding.Key, emergencyHotkey))
                return true;
        }

        return false;
    }

    private static FakeEmergencyHotkey CreateDefaultFakeHotkey(string binding)
    {
        var fake = new FakeEmergencyHotkey(binding);
        _ = fake.TryRegister(static () => { });
        return fake;
    }

    private DispatchRecord RecordAndReturnLocked(ActionDecision action, DispatchOutcome outcome, string? detail)
    {
        var record = new DispatchRecord(_time.GetUtcNow(), action.RuleId, action.AbilityId, action.Key, outcome, detail);
        _recentHistory.Add(record);
        while (_recentHistory.Count > MaxHistory)
            _recentHistory.RemoveAt(0);
        return record;
    }

    private sealed class NullLoggerFactory : ILoggerFactory
    {
        public static readonly NullLoggerFactory Instance = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void Dispose() { }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => false;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }
    }
}
