using System.Collections.Concurrent;
using BotDs.Core;

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

    /// <summary>Cancelled by controller state change.</summary>
    Cancelled,

    /// <summary>Coordinator is not armed.</summary>
    NotArmed,
}

/// <summary>
/// Serialized action coordinator. Consumes evaluation results while armed,
/// enforces rate limits, validates preconditions, and tracks pending
/// actions with bounded acknowledgement timeouts.
/// 
/// In DryRun mode: all checks run but no input is dispatched.
/// In Disabled/Live mode: as documented.
/// </summary>
public sealed class ActionCoordinator
{
    // ── Configurable bounds ──────────────────────────────────
    private readonly TimeSpan _ackTimeout;
    private readonly TimeSpan _minGlobalInterval;
    private readonly TimeSpan _minPerKeyInterval;
    // ── Dependencies ─────────────────────────────────────────
    private readonly SnapshotPublisher _publisher;
    private readonly ControllerStateMachine _stateMachine;
    private readonly ProfileService _profiles;
    private readonly ArmingReadinessService _readiness;
    private readonly TimeProvider _time;
    private readonly ILogger<ActionCoordinator> _log;

    // ── State ────────────────────────────────────────────────
    private readonly object _lock = new();
    private OutputMode _outputMode = OutputMode.Disabled;

    // Rate limit state
    private DateTimeOffset _lastDispatchUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _perKeyLastDispatch = new(StringComparer.OrdinalIgnoreCase);

    // Pending action tracking
    private ActionDecision? _pendingAction;
    private DateTimeOffset _pendingActionDispatchedUtc = DateTimeOffset.MinValue;
    private ulong _preDispatchHighWaterSeq;

    // History
    private readonly List<DispatchRecord> _recentHistory = [];
    private const int MaxHistory = 200;

    // Telemetry fence — ensures no new read cycle starts during dispatch
    private readonly SemaphoreSlim _dispatchFence = new(1, 1);

    public ActionCoordinator(
        SnapshotPublisher publisher,
        ControllerStateMachine stateMachine,
        ProfileService profiles,
        ArmingReadinessService readiness,
        IConfiguration configuration,
        TimeProvider? timeProvider = null,
        ILogger<ActionCoordinator>? log = null)
    {
        _publisher = publisher;
        _stateMachine = stateMachine;
        _profiles = profiles;
        _readiness = readiness;
        _time = timeProvider ?? TimeProvider.System;
        _log = log ?? NullLoggerFactory.Instance.CreateLogger<ActionCoordinator>();

        _ackTimeout = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Action:AcknowledgementTimeoutMs", 2000));
        _minGlobalInterval = TimeSpan.FromMilliseconds(
            (int)(1000.0 / configuration.GetValue<int>("BotDs:Action:MaxGlobalPerSecond", 4)));
        _minPerKeyInterval = TimeSpan.FromMilliseconds(
            (int)(1000.0 / configuration.GetValue<int>("BotDs:Action:MaxPerKeyPerSecond", 2)));
    }

    // ── Public API ───────────────────────────────────────────

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

    /// <summary>
    /// Attempt to arm the coordinator. Fails if output mode is not DryRun or Live,
    /// or if readiness check fails.
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
                _log.LogInformation("Output mode set to Disabled");
                return true;
            }

            if (_outputMode != OutputMode.Disabled)
            {
                _log.LogWarning("Cannot change output mode from {Current} to {Requested} — disarm first",
                    _outputMode, mode);
                return false;
            }

            ReadinessResult ready = _readiness.Evaluate(maxTelemetryAge);
            if (!ready.CanArm)
            {
                _log.LogWarning("Readiness check failed for mode {Mode}: {Blockers}",
                    mode, string.Join("; ", ready.Blockers));
                return false;
            }

            _outputMode = mode;
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
            _log.LogInformation("Output mode set to Disabled");
            return true;
        }
    }

    /// <summary>
    /// Consume an evaluation result. Called by EvaluatorLoop on each tick.
    /// Returns a dispatch record if a dispatch was attempted, null otherwise.
    /// </summary>
    public DispatchRecord? Consume(EvaluationResult result, long controllerGeneration)
    {
        // Quick exit if disabled
        OutputMode mode;
        lock (_lock) { mode = _outputMode; }
        if (mode == OutputMode.Disabled)
            return null;

        // Must have an action
        if (!result.HasAction || result.Action is null)
        {
            CheckPendingTimeout();
            return null;
        }

        return TryDispatch(result.Action, controllerGeneration);
    }

    // ── Dispatch logic ───────────────────────────────────────

    private DispatchRecord? TryDispatch(ActionDecision action, long controllerGeneration)
    {
        var now = _time.GetUtcNow();

        // Acquire the telemetry fence — blocks new read cycles during dispatch
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
                    return RecordAndReturn(action, DispatchOutcome.NotArmed, "Output disabled");

                // ── Pre-dispatch revalidation ──────────────────
                // Check pending action hasn't timed out
                if (_pendingAction is not null)
                {
                    TimeSpan elapsed = now - _pendingActionDispatchedUtc;
                    if (elapsed > _ackTimeout)
                    {
                        var timeout = RecordAndReturnLocked(
                            _pendingAction, DispatchOutcome.AcknowledgementTimeout,
                            $"Pending action timed out after {elapsed.TotalMilliseconds:F0}ms");
                        CancelPendingLocked("Acknowledgement timeout");
                        // Fall through to try the new action
                    }
                    else
                    {
                        return RecordAndReturnLocked(
                            action, DispatchOutcome.PendingActionBlocked,
                            $"Pending action awaiting acknowledgement ({elapsed.TotalMilliseconds:F0}ms of {_ackTimeout.TotalMilliseconds:F0}ms)");
                    }
                }

                // Rate limit — global
                TimeSpan globalElapsed = now - _lastDispatchUtc;
                if (globalElapsed < _minGlobalInterval)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.RateLimited,
                        $"Global rate limit ({_minGlobalInterval.TotalMilliseconds:F0}ms min), elapsed {globalElapsed.TotalMilliseconds:F0}ms");
                }

                // Rate limit — per-key
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

                // Revalidate controller generation hasn't changed
                if (_stateMachine.Generation != controllerGeneration)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.Cancelled,
                        "Controller generation changed — evaluation is stale");
                }

                // Revalidate controller state is armed
                var state = _stateMachine.State;
                if (state is ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
                {
                    return RecordAndReturnLocked(
                        action, DispatchOutcome.Cancelled,
                        $"Controller state is {state}");
                }

                // ── Dispatch (or dry-run log) ─────────────────
                if (_outputMode == OutputMode.DryRun)
                {
                    _log.LogInformation(
                        "[DRY-RUN] Action: Rule={RuleId}, Ability={AbilityId}, Key={Key}, Ack={Ack}, Seq={Seq}",
                        action.RuleId, action.AbilityId, action.Key, action.Acknowledgement, action.FrameSequence);
                }
                // Live mode (M7): actual SendInput goes here

                // Record dispatch
                _lastDispatchUtc = now;
                _perKeyLastDispatch[action.Key] = now;
                _pendingAction = action;
                _pendingActionDispatchedUtc = now;
                _preDispatchHighWaterSeq = action.FrameSequence;

                return RecordAndReturnLocked(
                    action, DispatchOutcome.Dispatched,
                    _outputMode == OutputMode.DryRun ? "dry-run" : "live");
            }
        }
        finally
        {
            _dispatchFence.Release();
        }
    }

    /// <summary>
    /// Check if the pending action has timed out without a new evaluation cycle.
    /// Called on each Consume tick even when no action is produced.
    /// </summary>
    private void CheckPendingTimeout()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_pendingAction is not null)
            {
                TimeSpan elapsed = now - _pendingActionDispatchedUtc;
                if (elapsed > _ackTimeout)
                {
                    _log.LogWarning(
                        "Pending action {Action} timed out after {Elapsed:F0}ms",
                        _pendingAction.AbilityId, elapsed.TotalMilliseconds);
                    RecordAndReturnLocked(
                        _pendingAction, DispatchOutcome.AcknowledgementTimeout,
                        $"Timeout after {elapsed.TotalMilliseconds:F0}ms");
                    CancelPendingLocked("Acknowledgement timeout");
                }
            }
        }
    }

    /// <summary>
    /// Clear the pending action with a reason. Called on disarm/estop/disable.
    /// </summary>
    public void CancelPending(string reason)
    {
        lock (_lock) CancelPendingLocked(reason);
    }

    private void CancelPendingLocked(string reason)
    {
        if (_pendingAction is not null)
        {
            _log.LogInformation("Cancelled pending action {AbilityId}: {Reason}",
                _pendingAction.AbilityId, reason);
            _pendingAction = null;
            _pendingActionDispatchedUtc = DateTimeOffset.MinValue;
        }
    }

    // ── History ──────────────────────────────────────────────

    private DispatchRecord RecordAndReturn(ActionDecision action, DispatchOutcome outcome, string? detail)
    {
        lock (_lock) return RecordAndReturnLocked(action, outcome, detail);
    }

    private DispatchRecord RecordAndReturnLocked(ActionDecision action, DispatchOutcome outcome, string? detail)
    {
        var record = new DispatchRecord(_time.GetUtcNow(), action.RuleId, action.AbilityId, action.Key, outcome, detail);
        _recentHistory.Add(record);
        while (_recentHistory.Count > MaxHistory)
            _recentHistory.RemoveAt(0);
        return record;
    }

    // ── Null logger factory for optional logger ──────────────

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
