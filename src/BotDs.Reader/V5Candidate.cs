using BotDs.Reader.V5;

namespace BotDs.Reader;

/// <summary>
/// A candidate sentinel location discovered during memory scanning.
/// Contains base address and parsed frame metadata for both slots.
/// The address is internal and must never be exposed in metrics/diagnostics.
/// </summary>
internal sealed record V5Candidate
{
    /// <summary>Base address of the sentinel in target process memory.</summary>
    public required nint BaseAddress { get; init; }

    /// <summary>Parsed sentinel at this location.</summary>
    public required V5Sentinel Sentinel { get; init; }

    /// <summary>Parsed frame from buffer slot A (index 0). Null if CRC invalid or parse failed.</summary>
    public ParsedV5Frame? FrameA { get; init; }

    /// <summary>Parsed frame from buffer slot B (index 1). Null if CRC invalid or parse failed.</summary>
    public ParsedV5Frame? FrameB { get; init; }

    /// <summary>At least one slot is valid.</summary>
    public bool IsValid => FrameA is not null || FrameB is not null;

    /// <summary>The single valid frame, if exactly one slot is valid.</summary>
    public ParsedV5Frame? SoleFrame
    {
        get
        {
            if (FrameA is not null && FrameB is null) return FrameA;
            if (FrameB is not null && FrameA is null) return FrameB;
            return null;
        }
    }

    /// <summary>Both slots are valid.</summary>
    public bool HasBothFrames => FrameA is not null && FrameB is not null;

    /// <summary>
    /// Read buffer A bytes from process memory given a reader and the provided slot buffer.
    /// </summary>
    public void ReadSlotA(IMemoryReader reader, byte[] slotBuffer)
    {
        reader.ReadExact(
            BaseAddress + V5Constants.BufferAOffset,
            slotBuffer,
            V5Constants.BufferSlotSize);
    }

    /// <summary>
    /// Read buffer B bytes from process memory given a reader and the provided slot buffer.
    /// </summary>
    public void ReadSlotB(IMemoryReader reader, byte[] slotBuffer)
    {
        reader.ReadExact(
            BaseAddress + V5Constants.BufferBOffset,
            slotBuffer,
            V5Constants.BufferSlotSize);
    }
}
