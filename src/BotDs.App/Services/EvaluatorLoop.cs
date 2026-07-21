using BotDs.Core;

namespace BotDs.App.Services;

public sealed class EvaluatorLoop : BackgroundService
{
    private readonly SnapshotPublisher _publisher;
    private readonly ProfileService _profiles;
    private readonly ControllerStateMachine _stateMachine;
    private readonly ActionCoordinator? _actionCoordinator;
    private readonly ILogger<EvaluatorLoop> _log;
    private readonly TimeSpan _maxTelemetryAge;
    private readonly TimeSpan _evaluationInterval;
    private EvaluationResult? _lastResult;
    private readonly object _resultLock = new();

    public EvaluatorLoop(
        SnapshotPublisher publisher,
        ProfileService profiles,
        ControllerStateMachine stateMachine,
        IConfiguration configuration,
        ILogger<EvaluatorLoop> log,
        ActionCoordinator? actionCoordinator = null)
    {
        _publisher = publisher;
        _profiles = profiles;
        _stateMachine = stateMachine;
        _actionCoordinator = actionCoordinator;
        _log = log;
        _maxTelemetryAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
        _evaluationInterval = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:EvaluationIntervalMs", 100));
    }

    public EvaluationResult? LastResult
    {
        get { lock (_resultLock) return _lastResult; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CombatEvaluator evaluator = new(_maxTelemetryAge);
        _log.LogInformation(
            "EvaluatorLoop started (interval={IntervalMs}ms, maxAge={MaxAgeMs}ms)",
            _evaluationInterval.TotalMilliseconds,
            _maxTelemetryAge.TotalMilliseconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                long generation = _stateMachine.Generation;
                ControllerState state = _stateMachine.State;

                if (state is ControllerState.Disarmed or ControllerState.Stopped or ControllerState.Faulted)
                {
                    await Task.Delay(_evaluationInterval, stoppingToken);
                    continue;
                }

                CombatProfile? profile = _profiles.ActiveProfile;
                if (profile is null)
                {
                    _stateMachine.EmergencyStop("No active profile.");
                    await Task.Delay(_evaluationInterval, stoppingToken);
                    continue;
                }

                TelemetryFrame frame = _publisher.Latest;
                EvaluationResult result = evaluator.Evaluate(profile, frame);

                if (_stateMachine.ApplyEvaluation(result, generation))
                {
                    lock (_resultLock) _lastResult = result;

                    if (_actionCoordinator is not null)
                    {
                        // Always observe pending ack/timeout; Consume also handles new actions.
                        DispatchRecord? dispatch = result.HasAction
                            ? _actionCoordinator.Consume(result, generation)
                            : _actionCoordinator.ObservePending(frame, generation);

                        if (dispatch is not null)
                        {
                            _log.LogInformation(
                                "Dispatch {Outcome}: Rule={RuleId}, Ability={AbilityId}, Key={Key}, Detail={Detail}",
                                dispatch.Outcome, dispatch.RuleId, dispatch.AbilityId, dispatch.Key, dispatch.Detail ?? "—");
                        }
                    }
                    else if (result.HasAction)
                    {
                        _log.LogInformation(
                            "Action pending (no coordinator): Rule={RuleId}, Ability={AbilityId}, Key={Key}, Ack={Ack}, Seq={Seq}",
                            result.Action!.RuleId,
                            result.Action.AbilityId,
                            result.Action.Key,
                            result.Action.Acknowledgement,
                            result.Action.FrameSequence);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Evaluator loop faulted");
                _stateMachine.EmergencyStop("Evaluator loop faulted.");
            }

            await Task.Delay(_evaluationInterval, stoppingToken);
        }
    }
}
