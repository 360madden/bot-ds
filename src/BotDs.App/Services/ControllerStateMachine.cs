using BotDs.Core;

namespace BotDs.App.Services;

public sealed class ControllerStateMachine
{
    private ControllerState _state = ControllerState.Disarmed;
    private StopReason _stopReason = StopReason.None;
    private string? _message;
    private ActionDecision? _pendingAction;
    private long _generation;
    private bool _configurationInProgress;
    private readonly object _lock = new();
    private readonly ILogger<ControllerStateMachine> _log;

    public ControllerStateMachine(ILogger<ControllerStateMachine> log)
    {
        _log = log;
    }

    public (ControllerState State, StopReason StopReason, string? Message, ActionDecision? PendingAction) Snapshot
    {
        get
        {
            lock (_lock)
                return (_state, _stopReason, _message, _pendingAction);
        }
    }

    public ControllerState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>
    /// Monotonically increasing generation counter. Captured by <see cref="EvaluatorLoop"/>
    /// before starting an evaluation; <see cref="ApplyEvaluation"/> rejects results whose
    /// generation no longer matches, preventing stale work from a prior arm cycle from being
    /// applied after disarm/profile-change/re-arm boundaries.
    /// </summary>
    public long Generation
    {
        get { lock (_lock) return _generation; }
    }

    /// <summary>
    /// Begins an exclusive logical configuration operation while the controller is disarmed.
    /// The returned lease may be held across awaits and must be disposed to permit arming.
    /// </summary>
    public IDisposable? TryBeginConfiguration()
    {
        lock (_lock)
        {
            if (_state != ControllerState.Disarmed || _configurationInProgress)
                return null;

            _configurationInProgress = true;
            _generation++;
            _log.LogInformation("Configuration begun (gen={Generation})", _generation);
            return new ConfigurationLease(this);
        }
    }

    public bool Arm()
    {
        lock (_lock)
        {
            if (_state is ControllerState.Stopped or ControllerState.Faulted)
                return false;
            if (_state == ControllerState.Disarmed && !_configurationInProgress)
            {
                _state = ControllerState.WaitingForPlayer;
                _stopReason = StopReason.None;
                _message = null;
                _pendingAction = null;
                _generation++;
                _log.LogInformation("Armed -> WaitingForPlayer (gen={Generation})", _generation);
                return true;
            }
            return false;
        }
    }

    public bool Disarm()
    {
        lock (_lock)
        {
            if (_state is ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
                return false;
            _state = ControllerState.Disarmed;
            _stopReason = StopReason.UserRequested;
            _message = "Disarmed by user.";
            _pendingAction = null;
            _generation++;
            _log.LogInformation("Disarmed (gen={Generation})", _generation);
            return true;
        }
    }

    public bool EmergencyStop(string? reason = null)
        => Stop(StopReason.EmergencyStop, reason ?? "Emergency stop activated.");

    /// <summary>
    /// Latches the controller into <see cref="ControllerState.Stopped"/> with an explicit reason.
    /// Used for emergency stop, unacknowledged actions, and other fail-closed outcomes.
    /// </summary>
    public bool Stop(StopReason reason, string? message = null)
    {
        if (reason is StopReason.None)
            throw new ArgumentOutOfRangeException(nameof(reason), "Stop requires a non-None reason.");

        lock (_lock)
        {
            if (_state is ControllerState.Stopped or ControllerState.Faulted)
                return false;
            _state = ControllerState.Stopped;
            _stopReason = reason;
            _message = message ?? reason.ToString();
            _pendingAction = null;
            _generation++;
            _log.LogWarning("Stopped ({Reason}): {Message} (gen={Generation})", reason, _message, _generation);
            return true;
        }
    }

    public bool ClearStop()
    {
        lock (_lock)
        {
            if (_state != ControllerState.Stopped)
                return false;
            _state = ControllerState.Disarmed;
            _stopReason = StopReason.None;
            _message = null;
            _pendingAction = null;
            _generation++;
            _log.LogInformation("Cleared stop -> Disarmed (gen={Generation})", _generation);
            return true;
        }
    }

    /// <summary>
    /// Applies an evaluation result only if <paramref name="expectedGeneration"/> matches the
    /// current <see cref="Generation"/>. This prevents stale results produced before a
    /// disarm/profile-change/re-arm boundary from being applied after the boundary.
    /// </summary>
    public bool ApplyEvaluation(EvaluationResult result, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_lock)
        {
            if (_generation != expectedGeneration)
                return false;

            if (_state is ControllerState.Stopped or ControllerState.Faulted)
                return false;

            // A stale evaluation computed before or concurrently with Disarm
            // must not move the controller out of Disarmed.
            if (_state == ControllerState.Disarmed)
                return false;

            if (result.StopReason != StopReason.None)
            {
                // Any non-None StopReason must leave the controller Stopped,
                // even if result.State is inconsistent.
                _state = ControllerState.Stopped;
                _stopReason = result.StopReason;
                _message = result.Message;
                _pendingAction = null;
            }
            else
            {
                _state = result.State;
                _pendingAction = result.Action;
            }

            return true;
        }
    }

    private void CompleteConfiguration()
    {
        lock (_lock)
        {
            _configurationInProgress = false;
            _generation++;
            _log.LogInformation("Configuration completed (gen={Generation})", _generation);
        }
    }

    private sealed class ConfigurationLease(ControllerStateMachine owner) : IDisposable
    {
        private ControllerStateMachine? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.CompleteConfiguration();
        }
    }
}
