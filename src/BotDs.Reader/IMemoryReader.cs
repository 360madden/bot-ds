namespace BotDs.Reader;

/// <summary>
/// Structured region enumeration failure cause.
/// </summary>
public enum RegionEnumerationFailure
{
    None = 0,
    VirtualQueryError,
    OverflowOrBackward,
    ProcessExit,
}

public sealed record RegionEnumerationResult(
    IReadOnlyList<MemoryRegion> Regions,
    bool IsComplete,
    RegionEnumerationFailure FailureCause = RegionEnumerationFailure.None,
    int NativeErrorCode = 0);

public interface IMemoryReader : IDisposable
{
    int ProcessId { get; }
    bool IsAlive { get; }
    void ReadExact(nint address, byte[] buffer, int size);
    RegionEnumerationResult QueryReadableRegions(CancellationToken cancellationToken = default);
    bool CheckLiveness();
}

public interface IMemoryReaderFactory
{
    /// <summary>
    /// Open a process by PID. If <paramref name="expectedName"/> is non-null,
    /// the implementation must verify the opened process image basename matches
    /// (case-insensitive, normalized). PID-only callers pass null.
    /// </summary>
    IMemoryReader Open(int processId, string? expectedName = null);
}
