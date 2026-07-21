namespace BotDs.Input;

/// <summary>
/// Global emergency-stop hotkey registration (PLAN.md §5.6 / M8).
/// </summary>
public interface IEmergencyHotkey : IDisposable
{
    /// <summary>Configured binding string, e.g. "Ctrl+Shift+F12".</summary>
    string Binding { get; }

    /// <summary>True when the OS hotkey is currently registered.</summary>
    bool IsRegistered { get; }

    /// <summary>Last registration failure detail, if any.</summary>
    string? LastError { get; }

    /// <summary>
    /// Register or re-register the hotkey. Invokes <paramref name="onTriggered"/>
    /// on the hotkey message thread when pressed (must be thread-safe).
    /// </summary>
    bool TryRegister(Action onTriggered);

    /// <summary>Unregister the hotkey if registered.</summary>
    void Unregister();
}

/// <summary>
/// Test double that never touches Win32. Call <see cref="SimulateTrigger"/> to fire.
/// </summary>
public sealed class FakeEmergencyHotkey : IEmergencyHotkey
{
    private Action? _onTriggered;
    private bool _disposed;

    public FakeEmergencyHotkey(string binding = "Ctrl+Shift+F12")
    {
        Binding = binding;
    }

    public string Binding { get; }
    public bool IsRegistered { get; private set; }
    public string? LastError { get; private set; }
    public int TriggerCount { get; private set; }

    public bool TryRegister(Action onTriggered)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(onTriggered);

        if (!VirtualKeyMap.TryParseHotkey(Binding, out _, out _, out string? error))
        {
            LastError = error;
            IsRegistered = false;
            return false;
        }

        _onTriggered = onTriggered;
        IsRegistered = true;
        LastError = null;
        return true;
    }

    public void Unregister()
    {
        IsRegistered = false;
        _onTriggered = null;
    }

    public void SimulateTrigger()
    {
        if (!IsRegistered || _onTriggered is null)
            return;
        TriggerCount++;
        _onTriggered.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Unregister();
        _disposed = true;
    }
}
