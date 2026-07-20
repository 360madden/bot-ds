namespace BotDs.Input;

/// <summary>
/// Narrow keyboard output interface. Dispatch a pre-validated key chord.
/// Returns true if the key event was successfully injected; false if blocked
/// by a precondition failure (foreground mismatch, held key, etc.).
/// </summary>
public interface IKeySink
{
    /// <summary>
    /// Dispatch a single key chord (key-down followed by key-up).
    /// Preconditions: foreground window must belong to the captured process,
    /// no chord keys should be physically held, and the sink must not be
    /// faulted or disposed.
    /// </summary>
    /// <param name="key">Key binding string (e.g. "1", "Shift+F1").</param>
    /// <param name="ct">Cancellation token for graceful shutdown.</param>
    /// <returns>True if the chord was dispatched; false if blocked.</returns>
    bool DispatchKey(string key, CancellationToken ct = default);

    /// <summary>
    /// True when the sink is ready to accept dispatch requests.
    /// False during initialization, fault, or after disposal.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// The PID of the foreground process this sink is bound to,
    /// or 0 if not bound.
    /// </summary>
    int BoundPid { get; }

    /// <summary>
    /// Latch an unrecoverable fault. Once faulted, all DispatchKey
    /// calls return false. Call this when the foreground changes
    /// mid-dispatch or cleanup fails.
    /// </summary>
    void LatchFault(string reason);
}

/// <summary>
/// Parses and validates key binding strings for the input sink.
/// Separates modifiers (Shift/Ctrl/Alt) from the target key.
/// </summary>
public static class KeyBindingGrammar
{
    /// <summary>
    /// Parse a key binding string into modifiers and the target virtual key name.
    /// Returns null if the binding is invalid.
    /// </summary>
    public static (HashSet<string> Modifiers, string Key)? Parse(string binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
            return null;

        string[] parts = binding.Trim().Split('+');
        if (parts.Length == 0 || parts.Length > 4)
            return null;

        string key = parts[^1];
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!IsAllowedModifier(parts[i]))
                return null;
            if (!modifiers.Add(parts[i]))
                return null; // duplicate modifier
        }

        return (modifiers, key);
    }

    private static bool IsAllowedModifier(string m) =>
        string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Fake key sink for testing and dry-run operation.
/// Records dispatches for assertion but performs no Windows input.
/// </summary>
public sealed class FakeKeySink : IKeySink
{
    private volatile bool _faulted;
    private readonly object _lock = new();
    private readonly List<FakeDispatchRecord> _history = [];

    public bool IsReady => !_faulted;
    public int BoundPid => 0;

    public IReadOnlyList<FakeDispatchRecord> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public bool DispatchKey(string key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_faulted) return false;

            var parsed = KeyBindingGrammar.Parse(key);
            if (parsed is null) return false;

            _history.Add(new FakeDispatchRecord(
                DateTimeOffset.UtcNow,
                key,
                parsed.Value.Modifiers,
                parsed.Value.Key));
            return true;
        }
    }

    public void LatchFault(string reason)
    {
        lock (_lock) _faulted = true;
    }

    public void ClearHistory()
    {
        lock (_lock) _history.Clear();
    }
}

public sealed record FakeDispatchRecord(
    DateTimeOffset Timestamp,
    string RawBinding,
    HashSet<string> Modifiers,
    string Key);
