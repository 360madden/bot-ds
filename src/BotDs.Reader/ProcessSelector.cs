namespace BotDs.Reader;

/// <summary>
/// Describes a target process for memory attachment.
/// PID is authoritative. PID+name asserts exact name. Name-only requires exactly one match.
/// </summary>
public sealed record ProcessSelector
{
    private string? _processName;

    /// <summary>Optional explicit process ID. Must be positive.</summary>
    public int? ProcessId { get; init; }

    /// <summary>
    /// Optional normalized process basename. Must pass validation: non-empty, no path separators or wildcards.
    /// </summary>
    public string? ProcessName
    {
        get => _processName;
        init
        {
            if (value is not null)
            {
                ValidateName(value);
                _processName = value;
            }
        }
    }

    public bool IsValid => ProcessId is > 0 || (ProcessId is null && !string.IsNullOrWhiteSpace(ProcessName));

    public static string NormalizeName(string name)
    {
        ReadOnlySpan<char> span = name.Trim();
        if (span.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            span = span[..^4];
        return span.ToString().ToLowerInvariant();
    }

    public bool NameMatches(string processImageName)
    {
        if (ProcessName is null) return true;
        return string.Equals(NormalizeName(ProcessName), NormalizeName(processImageName), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reject names with path separators, wildcards, or empty normalized forms.
    /// </summary>
    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("Process name must not be empty.", nameof(ProcessName));
        if (trimmed.Contains('\\') || trimmed.Contains('/'))
            throw new ArgumentException("Process name must not contain path separators.", nameof(ProcessName));
        if (trimmed.Contains('*') || trimmed.Contains('?'))
            throw new ArgumentException("Process name must not contain wildcards.", nameof(ProcessName));
        if (NormalizeName(trimmed).Length == 0)
            throw new ArgumentException("Process name must not be empty after normalization.", nameof(ProcessName));
    }

    public override string ToString()
    {
        if (ProcessId.HasValue && ProcessName is not null) return $"PID={ProcessId.Value}";
        if (ProcessId.HasValue) return $"PID={ProcessId.Value}";
        return "Name=(sanitized)";
    }
}
