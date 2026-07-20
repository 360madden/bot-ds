using BotDs.Core;

namespace BotDs.Reader.V5;

/// <summary>
/// Tracks session and sequence continuity across frames using IETF RFC 1982
/// serial-number arithmetic for uint wrap ordering (half-range comparison).
/// Ambiguous ordering (diff == 0x80000000) is treated fail-closed.
/// </summary>
public sealed class SessionTracker
{
    private Guid _sessionId;
    private uint _lastSequence;
    private uint _trustedHighWaterMark;
    private bool _isDegraded;
    private bool _hasFirstFrame;

    public Guid SessionId => _sessionId;
    public uint LastSequence => _lastSequence;
    public uint TrustedHighWaterMark => _trustedHighWaterMark;
    public bool IsDegraded => _isDegraded;

    /// <summary>Returns true if <paramref name="b"/> is after <paramref name="a"/> in wrap-aware uint ordering.</summary>
    internal static bool IsAfter(uint b, uint a) => b != a && unchecked(b - a) < 0x80000000;

    /// <summary>Returns true if <paramref name="b"/> is before <paramref name="a"/> in wrap-aware uint ordering.</summary>
    internal static bool IsBefore(uint b, uint a) => b != a && unchecked(b - a) > 0x80000000;

    /// <summary>Returns true if the ordering is ambiguous (exactly half the range).</summary>
    internal static bool IsAmbiguous(uint b, uint a) => b != a && unchecked(b - a) == 0x80000000;

    /// <summary>
    /// Evaluate continuity for a new frame. Returns the resulting health classification.
    /// </summary>
    public ContinuityResult Evaluate(ParsedV5Frame frame, out uint gapSize)
    {
        gapSize = 0;

        if (frame.Provider is not { } provider)
        {
            _lastSequence = frame.Header.Sequence;
            _trustedHighWaterMark = _lastSequence;
            return ContinuityResult.Valid;
        }

        // First frame — establish session baseline
        if (!_hasFirstFrame)
        {
            _sessionId = provider.SessionId;
            _lastSequence = frame.Header.Sequence;
            _trustedHighWaterMark = _lastSequence;
            _hasFirstFrame = true;
            return ContinuityResult.Valid;
        }

        // Session restart detected
        if (provider.SessionId != _sessionId)
        {
            _sessionId = provider.SessionId;
            _lastSequence = frame.Header.Sequence;
            _trustedHighWaterMark = _lastSequence;
            _isDegraded = false;
            return ContinuityResult.SessionRestart;
        }

        uint currentSeq = frame.Header.Sequence;
        uint lastSeq = _lastSequence;

        // Duplicate sequence (unchanged buffer) flows through to normal
        // evaluation so staleness detection in StableReader can fire.
        // If currently degraded, duplicates below the high-water mark are
        // caught by the degraded guard below.

        // If degraded: reject any sequence not clearly after the trusted high-water mark
        if (_isDegraded)
        {
            if (!IsAfter(currentSeq, _trustedHighWaterMark))
                return ContinuityResult.SequenceDecrement;

            // Recovered — clear degraded state
            _isDegraded = false;
        }

        // Ambiguous ordering (exactly half the range) — fail closed
        if (IsAmbiguous(currentSeq, lastSeq))
        {
            _isDegraded = true;
            return ContinuityResult.SequenceDecrement;
        }

        // Sequence went backwards — protocol violation
        // Do NOT lower the trusted high-water mark
        if (IsBefore(currentSeq, lastSeq))
        {
            _isDegraded = true;
            return ContinuityResult.SequenceDecrement;
        }

        // Exact duplicate while not degraded: valid unchanged so freshness can age it.
        // Must be checked before the gap/wrap arithmetic because lastSeq+1 wrapping
        // at uint.MaxValue would otherwise produce a spurious huge gap.
        if (currentSeq == lastSeq)
        {
            return ContinuityResult.Valid;
        }

        // currentSeq is after lastSeq: it is either a normal forward increment or a wrap.
        if (currentSeq < lastSeq) // raw comparison detects wrap from ~max to ~0
        {
            // Gap across the wrap boundary
            gapSize = uint.MaxValue - lastSeq + currentSeq;
            _lastSequence = currentSeq;
            _trustedHighWaterMark = currentSeq;
            return ContinuityResult.SequenceWrap;
        }

        // Normal forward increment: check for gap
        uint expected = lastSeq + 1;
        if (currentSeq > expected)
        {
            gapSize = currentSeq - expected;
            _lastSequence = currentSeq;
            _trustedHighWaterMark = currentSeq;
            return ContinuityResult.Gap;
        }

        // Normal consecutive increment
        _lastSequence = currentSeq;
        _trustedHighWaterMark = currentSeq;
        return ContinuityResult.Valid;
    }

    public void Reset()
    {
        _sessionId = Guid.Empty;
        _lastSequence = 0;
        _trustedHighWaterMark = 0;
        _isDegraded = false;
        _hasFirstFrame = false;
    }
}

public enum ContinuityResult
{
    Valid,
    Gap,
    SequenceDecrement,
    SessionRestart,
    SequenceWrap,
}

/// <summary>
/// Result of a double-buffer read cycle. Contains the selected frame and transport health.
/// </summary>
public sealed record StableReadResult(
    ParsedV5Frame? Frame,
    ProviderHealth TransportHealth,
    ContinuityResult Continuity,
    uint GapSize,
    string? FailureDetail,
    TimeSpan Age = default)
{
    public bool IsUsable => Frame is not null && TransportHealth == ProviderHealth.Healthy;

    public static StableReadResult Disconnected(string? detail = null) =>
        new(null, ProviderHealth.Disconnected, ContinuityResult.Valid, 0, detail);

    public static StableReadResult Faulted(string? detail = null) =>
        new(null, ProviderHealth.Faulted, ContinuityResult.Valid, 0, detail);

    public static StableReadResult Healthy(
        ParsedV5Frame frame,
        TimeSpan age = default,
        ContinuityResult continuity = ContinuityResult.Valid) =>
        new(frame, ProviderHealth.Healthy, continuity, 0, null, age);

    public static StableReadResult Degraded(
        ParsedV5Frame frame,
        ContinuityResult continuity,
        uint gapSize,
        TimeSpan age = default) =>
        new(frame, ProviderHealth.Degraded, continuity, gapSize, null, age);

    public static StableReadResult Stale(ParsedV5Frame frame, TimeSpan age, string? detail = null) =>
        new(frame, ProviderHealth.Stale, ContinuityResult.Valid, 0, detail, age);
}

/// <summary>
/// High-level stable reader that manages the double-buffer protocol.
///
/// On each read cycle:
/// 1. Copies both buffers into local memory (atomic copy, no shared writer state).
/// 2. Validates CRC on each buffer independently.
/// 3. Parses each CRC-valid buffer independently.
/// 4. Selects a frame by same-session sequence or safely comparable producer time.
/// 5. Evaluates sequence/session continuity.
/// 6. Checks frame staleness.
/// 7. Maps transport-level health to ProviderHealth.
/// </summary>
public sealed class StableReader
{
    private readonly SessionTracker _sessionTracker = new();
    private readonly TimeSpan? _localMaxAge;
    private readonly TimeProvider _timeProvider;

    // Cached buffers to avoid reallocation
    private readonly byte[] _bufferA = new byte[V5Constants.BufferSlotSize];
    private readonly byte[] _bufferB = new byte[V5Constants.BufferSlotSize];

    private uint _lastObservedSequence;
    private long _lastSequenceChangeTimestamp;
    private bool _hasObservedSequence;
    private int _consecutiveFaults;

    /// <param name="localMaxAge">Override for MaxTelemetryAgeMs; if null, uses the emitter's reported value.</param>
    /// <param name="timeProvider">Optional monotonic TimeProvider for freshness timing; defaults to TimeProvider.System.</param>
    public StableReader(TimeSpan? localMaxAge = null, TimeProvider? timeProvider = null)
    {
        _localMaxAge = localMaxAge;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Perform one read cycle. The caller provides callbacks to read raw buffer data
    /// from the transport layer (process memory, shared memory, or test harness).
    /// </summary>
    /// <param name="readBufferA">Read the full 8192-byte Buffer A into the provided span.</param>
    /// <param name="readBufferB">Read the full 8192-byte Buffer B into the provided span.</param>
    /// <param name="wallClockNow">Current UTC time for staleness checks.</param>
    public StableReadResult Read(
        Action<Span<byte>> readBufferA,
        Action<Span<byte>> readBufferB,
        DateTimeOffset wallClockNow)
    {
        // 1. Copy both buffers into local memory
        Span<byte> localA = _bufferA.AsSpan();
        Span<byte> localB = _bufferB.AsSpan();
        localA.Clear();
        localB.Clear();

        try
        {
            readBufferA(localA);
            readBufferB(localB);
        }
        catch (Exception ex)
        {
            _consecutiveFaults++;
            return StableReadResult.Disconnected($"Buffer read failed: {ex.Message}");
        }

        // 2. Validate CRC on each buffer
        bool aValid = V5Crc32.ValidateBuffer(localA, out _);
        bool bValid = V5Crc32.ValidateBuffer(localB, out _);

        if (!aValid && !bValid)
        {
            _consecutiveFaults++;
            return StableReadResult.Faulted(
                $"Both buffers have CRC mismatch (consecutive faults: {_consecutiveFaults})");
        }

        // 3. Parse both CRC-valid candidates before selection.
        ParsedV5Frame? frameA = ParseCandidate(localA, aValid, 0, out string failureA);
        ParsedV5Frame? frameB = ParseCandidate(localB, bValid, 1, out string failureB);

        if (frameA is null && frameB is null)
        {
            _consecutiveFaults++;
            return StableReadResult.Faulted($"No valid frame. Buffer A: {failureA}; Buffer B: {failureB}");
        }

        // 4. Sequence numbers are comparable only within one session using
        // wrap-aware ordering (IETF RFC 1982 serial-number arithmetic).
        // Across sessions, producer frame time is compared with uint wrap semantics.
        ParsedV5Frame frame;
        if (frameA is not null && frameB is not null)
        {
            if (frameA.Provider!.SessionId == frameB.Provider!.SessionId)
            {
                uint seqA = frameA.Header.Sequence;
                uint seqB = frameB.Header.Sequence;
                if (SessionTracker.IsAmbiguous(seqB, seqA))
                {
                    _consecutiveFaults++;
                    return StableReadResult.Faulted(
                        $"Ambiguous same-session sequence ordering: seqA={seqA}, seqB={seqB}");
                }
                frame = SessionTracker.IsAfter(seqB, seqA) ? frameB : frameA;
            }
            else
            {
                int producerTimeOrder = CompareProducerFrameTime(
                    frameA.Header.ProducerFrameMs,
                    frameB.Header.ProducerFrameMs);
                if (producerTimeOrder == 0)
                {
                    _consecutiveFaults++;
                    return StableReadResult.Faulted(
                        $"Ambiguous sessions: buffer A session {frameA.Provider.SessionId:D} at producer frame {frameA.Header.ProducerFrameMs}; " +
                        $"buffer B session {frameB.Provider.SessionId:D} at producer frame {frameB.Header.ProducerFrameMs}");
                }

                frame = producerTimeOrder > 0 ? frameA : frameB;
            }
        }
        else
        {
            frame = frameA ?? frameB!;
        }

        // 5. Evaluate continuity before freshness so a new session may reset sequence.
        ContinuityResult continuity = _sessionTracker.Evaluate(frame, out uint gapSize);

        if (continuity == ContinuityResult.SequenceDecrement)
        {
            return new StableReadResult(
                frame, ProviderHealth.Faulted, continuity, 0,
                "Sequence decremented within session");
        }

        _consecutiveFaults = 0;

        // 6. Freshness uses the reader's monotonic TimeProvider clock.
        // wallClockNow is preserved for caller compatibility but not used for duration.
        if (!_hasObservedSequence
            || continuity == ContinuityResult.SessionRestart
            || frame.Header.Sequence != _lastObservedSequence)
        {
            _lastObservedSequence = frame.Header.Sequence;
            _lastSequenceChangeTimestamp = _timeProvider.GetTimestamp();
            _hasObservedSequence = true;
        }

        TimeSpan age = _timeProvider.GetElapsedTime(_lastSequenceChangeTimestamp);

        uint producerMaximumAgeMs = frame.Provider?.MaxTelemetryAgeMs ?? 0;
        TimeSpan maximumAge = _localMaxAge
            ?? TimeSpan.FromMilliseconds(producerMaximumAgeMs > 0 ? producerMaximumAgeMs : 500);
        if (age > maximumAge)
        {
            return StableReadResult.Stale(
                frame,
                age,
                $"Sequence has not advanced for {age.TotalMilliseconds:F0}ms; maximum is {maximumAge.TotalMilliseconds:F0}ms");
        }

        // 7. Map to health
        if (continuity is ContinuityResult.Gap or ContinuityResult.SequenceWrap)
        {
            return StableReadResult.Degraded(frame, continuity, gapSize, age);
        }

        return StableReadResult.Healthy(frame, age, continuity);
    }

    /// <summary>
    /// Reset internal state (session tracker, cached sequences).
    /// </summary>
    public void Reset()
    {
        _sessionTracker.Reset();
        _lastObservedSequence = 0;
        _lastSequenceChangeTimestamp = 0;
        _hasObservedSequence = false;
        _consecutiveFaults = 0;
        Array.Clear(_bufferA);
        Array.Clear(_bufferB);
    }

    private static ParsedV5Frame? ParseCandidate(
        ReadOnlySpan<byte> buffer,
        bool crcValid,
        int bufferIndex,
        out string failure)
    {
        if (!crcValid)
        {
            failure = "CRC mismatch";
            return null;
        }

        V5ParseResult result = V5Parser.Parse(buffer, bufferIndex);
        if (!result.IsValid)
        {
            failure = $"{result.Failure} — {result.FailureDetail}";
            return null;
        }

        if (result.Frame?.Provider is null)
        {
            failure = "ProviderInfo section is required";
            return null;
        }

        failure = string.Empty;
        return result.Frame;
    }

    private static int CompareProducerFrameTime(uint a, uint b)
    {
        uint difference = unchecked(a - b);
        if (difference is 0 or 0x80000000)
            return 0;

        return difference < 0x80000000 ? 1 : -1;
    }

    /// <summary>
    /// Whether the session tracker is currently in a fault-degraded state
    /// (sequence decrement, replay, or ambiguous ordering).
    /// </summary>
    public bool IsSessionDegraded => _sessionTracker.IsDegraded;
}
