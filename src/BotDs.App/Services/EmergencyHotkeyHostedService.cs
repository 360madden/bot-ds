using BotDs.Input;

namespace BotDs.App.Services;

/// <summary>
/// Registers the global emergency-stop hotkey for the process lifetime and
/// latches controller stop + disables output when triggered.
/// </summary>
public sealed class EmergencyHotkeyHostedService : IHostedService
{
    private readonly IEmergencyHotkey _hotkey;
    private readonly ControllerStateMachine _stateMachine;
    private readonly ActionCoordinator _coordinator;
    private readonly ILogger<EmergencyHotkeyHostedService> _log;

    public EmergencyHotkeyHostedService(
        IEmergencyHotkey hotkey,
        ControllerStateMachine stateMachine,
        ActionCoordinator coordinator,
        ILogger<EmergencyHotkeyHostedService> log)
    {
        _hotkey = hotkey;
        _stateMachine = stateMachine;
        _coordinator = coordinator;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        bool ok = _hotkey.TryRegister(OnTriggered);
        if (ok)
        {
            _log.LogInformation("Emergency hotkey registered: {Binding}", _hotkey.Binding);
        }
        else
        {
            _log.LogWarning(
                "Emergency hotkey registration failed for {Binding}: {Error}. Live output will be blocked while unregistered.",
                _hotkey.Binding, _hotkey.LastError ?? "unknown");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _hotkey.Unregister();
        _log.LogInformation("Emergency hotkey unregistered: {Binding}", _hotkey.Binding);
        return Task.CompletedTask;
    }

    private void OnTriggered()
    {
        try
        {
            _log.LogWarning("Emergency hotkey pressed ({Binding}) — stopping controller", _hotkey.Binding);
            _coordinator.CancelPending("Emergency hotkey");
            _coordinator.Disable();
            _stateMachine.EmergencyStop($"Emergency hotkey ({_hotkey.Binding})");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Emergency hotkey handler faulted");
        }
    }
}
