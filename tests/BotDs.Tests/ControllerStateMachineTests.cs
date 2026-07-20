using BotDs.App.Services;
using BotDs.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

public sealed class ControllerStateMachineTests
{
    [Fact]
    public void EmergencyStop_RemainsLatchedWhenDisarmIsRequested()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());
        Assert.True(controller.EmergencyStop());

        Assert.False(controller.Disarm());

        Assert.Equal(ControllerState.Stopped, controller.State);
        Assert.Equal(StopReason.EmergencyStop, controller.Snapshot.StopReason);
    }

    [Fact]
    public void ClearStop_ReturnsControllerToDisarmed()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.EmergencyStop());

        Assert.True(controller.ClearStop());

        Assert.Equal(ControllerState.Disarmed, controller.State);
        Assert.Equal(StopReason.None, controller.Snapshot.StopReason);
    }

    [Fact]
    public void ApplyEvaluation_StaleResultCannotMoveControllerOutOfDisarmed()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);

        long generation = controller.Generation;

        // Simulate a stale evaluation: build it while armed, then disarm
        // before the evaluation is applied.
        var staleResult = new EvaluationResult(
            ControllerState.Evaluating,
            Action: null,
            Rejections: []);

        Assert.True(controller.Disarm());
        Assert.Equal(ControllerState.Disarmed, controller.State);

        // Apply the stale evaluation after disarm.
        Assert.False(controller.ApplyEvaluation(staleResult, generation));

        // Controller must remain Disarmed.
        Assert.Equal(ControllerState.Disarmed, controller.State);
    }

    [Fact]
    public void ApplyEvaluation_InconsistentStopReasonForcesStoppedState()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());

        long generation = controller.Generation;

        // Build a result with a non-None StopReason but a State that is NOT Stopped.
        var inconsistentResult = new EvaluationResult(
            ControllerState.WaitingForTarget,
            Action: null,
            Rejections: [],
            StopReason: StopReason.ProviderUnavailable,
            Message: "Provider went away.");

        Assert.True(controller.ApplyEvaluation(inconsistentResult, generation));

        // Regardless of result.State, a non-None StopReason must leave the controller Stopped.
        Assert.Equal(ControllerState.Stopped, controller.State);
        Assert.Equal(StopReason.ProviderUnavailable, controller.Snapshot.StopReason);
        Assert.Equal("Provider went away.", controller.Snapshot.Message);
        Assert.Null(controller.Snapshot.PendingAction);
    }

    [Fact]
    public void ApplyEvaluation_CannotClearLatchedEmergencyStop()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());
        Assert.True(controller.EmergencyStop("latched"));

        long generation = controller.Generation;

        Assert.False(controller.ApplyEvaluation(new EvaluationResult(
            ControllerState.Armed,
            Action: null,
            Rejections: []), generation));

        Assert.Equal(ControllerState.Stopped, controller.State);
        Assert.Equal(StopReason.EmergencyStop, controller.Snapshot.StopReason);
        Assert.Equal("latched", controller.Snapshot.Message);
    }

    [Fact]
    public void ApplyEvaluation_ValidGenerationIsApplied()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());

        long generation = controller.Generation;

        var result = new EvaluationResult(
            ControllerState.Evaluating,
            Action: null,
            Rejections: []);

        Assert.True(controller.ApplyEvaluation(result, generation));

        // The generation matches, so the evaluation should be applied.
        Assert.Equal(ControllerState.Evaluating, controller.State);
    }

    [Fact]
    public void Generation_AdvancesOnEveryLifecycleBoundary()
    {
        ControllerStateMachine controller = CreateController();
        long gen = controller.Generation;

        controller.Arm();
        Assert.True(controller.Generation > gen);
        gen = controller.Generation;

        controller.Disarm();
        Assert.True(controller.Generation > gen);
        gen = controller.Generation;

        controller.Arm();
        Assert.True(controller.Generation > gen);
        gen = controller.Generation;

        controller.EmergencyStop();
        Assert.True(controller.Generation > gen);
        gen = controller.Generation;

        controller.ClearStop();
        Assert.True(controller.Generation > gen);
    }

    [Fact]
    public void ApplyEvaluation_DisarmThenRearmRejectsStaleResult()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);

        // Capture generation from the first arm cycle.
        long staleGeneration = controller.Generation;

        var staleResult = new EvaluationResult(
            ControllerState.Evaluating,
            Action: null,
            Rejections: []);

        // Disarm then re-arm: generation advances twice.
        Assert.True(controller.Disarm());
        Assert.True(controller.Arm());
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);

        // Apply the stale evaluation from the first cycle.
        Assert.False(controller.ApplyEvaluation(staleResult, staleGeneration));

        // The stale generation no longer matches; controller must not change.
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);
        Assert.Null(controller.Snapshot.PendingAction);
    }

    [Fact]
    public void ApplyEvaluation_EmergencyStopClearRearmRejectsStaleResult()
    {
        ControllerStateMachine controller = CreateController();
        Assert.True(controller.Arm());

        long staleGeneration = controller.Generation;

        var staleResult = new EvaluationResult(
            ControllerState.Evaluating,
            Action: null,
            Rejections: []);

        // EmergencyStop → ClearStop → Arm: generation advances three times.
        Assert.True(controller.EmergencyStop("test"));
        Assert.True(controller.ClearStop());
        Assert.True(controller.Arm());
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);

        // Apply the stale evaluation from before the emergency stop.
        Assert.False(controller.ApplyEvaluation(staleResult, staleGeneration));

        // The stale generation no longer matches; controller must not change.
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);
        Assert.Null(controller.Snapshot.PendingAction);
    }

    [Fact]
    public void ConfigurationLease_PreventsArmUntilDisposed()
    {
        ControllerStateMachine controller = CreateController();
        IDisposable lease = Assert.IsAssignableFrom<IDisposable>(controller.TryBeginConfiguration());

        Assert.False(controller.Arm());

        lease.Dispose();
        Assert.True(controller.Arm());
    }

    [Fact]
    public void ConfigurationLease_DisposalIsIdempotent()
    {
        ControllerStateMachine controller = CreateController();
        IDisposable lease = Assert.IsAssignableFrom<IDisposable>(controller.TryBeginConfiguration());
        long begunGeneration = controller.Generation;

        lease.Dispose();
        long completedGeneration = controller.Generation;
        lease.Dispose();

        Assert.True(completedGeneration > begunGeneration);
        Assert.Equal(completedGeneration, controller.Generation);
        Assert.True(controller.Arm());
    }

    [Fact]
    public void ConfigurationLease_InvalidatesEvaluationAcrossConfigurationBoundary()
    {
        ControllerStateMachine controller = CreateController();
        long staleGeneration = controller.Generation;
        IDisposable lease = Assert.IsAssignableFrom<IDisposable>(controller.TryBeginConfiguration());
        lease.Dispose();
        Assert.True(controller.Arm());

        bool applied = controller.ApplyEvaluation(new EvaluationResult(
            ControllerState.Evaluating,
            Action: null,
            Rejections: []), staleGeneration);

        Assert.False(applied);
        Assert.Equal(ControllerState.WaitingForPlayer, controller.State);
    }

    [Fact]
    public void ConfigurationLease_CannotBeginOutsideDisarmedOrConcurrently()
    {
        ControllerStateMachine controller = CreateController();
        using IDisposable lease = Assert.IsAssignableFrom<IDisposable>(controller.TryBeginConfiguration());

        Assert.Null(controller.TryBeginConfiguration());

        lease.Dispose();
        Assert.True(controller.Arm());
        Assert.Null(controller.TryBeginConfiguration());
    }

    private static ControllerStateMachine CreateController() =>
        new(NullLogger<ControllerStateMachine>.Instance);
}
