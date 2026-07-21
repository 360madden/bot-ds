using System.Diagnostics;
using System.Runtime.InteropServices;
using BotDs.Reader.V5;

namespace BotDs.Reader;

public sealed record SentinelScannerOptions
{
    public int ChunkSizeBytes { get; init; } = 1_048_576;
    public int MaxRawMatches { get; init; } = 256;
    // Raised from 32: Lua GC leaves many BotDsV05 copies; RankByFreshness keeps the
    // highest-sequence subset so partial recovery still sees the live publisher.
    public int MaxCandidates { get; init; } = 64;

    public void Validate()
    {
        if (ChunkSizeBytes < 256 || ChunkSizeBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(ChunkSizeBytes));
        if (MaxRawMatches < 1 || MaxRawMatches > 10_000) throw new ArgumentOutOfRangeException(nameof(MaxRawMatches));
        if (MaxCandidates < 1 || MaxCandidates > 1_000) throw new ArgumentOutOfRangeException(nameof(MaxCandidates));
    }
}

[Flags]
internal enum ScanIncompleteCause
{
    None = 0,
    RegionEnumerationFailed = 1 << 0,
    ReadFailure = 1 << 1,
    RawMatchLimitExceeded = 1 << 2,
    CandidateLimitExceeded = 1 << 3,
}

internal static class V5SentinelScanner
{
    private static readonly byte[] MagicBytes = "BotDsV05"u8.ToArray();
    private const int MaxCarry = V5Constants.SentinelMagicLength - 1;

    public static ScanResult Scan(IMemoryReader reader, SentinelScannerOptions options,
        CancellationToken ct = default)
    {
        options.Validate();
        long t0 = Stopwatch.GetTimestamp();
        var en = reader.QueryReadableRegions(ct);
        var candidates = new List<V5Candidate>();
        long raw = 0, exact = 0, bytes = 0, readFails = 0;
        ScanIncompleteCause cause = en.IsComplete ? ScanIncompleteCause.None : ScanIncompleteCause.RegionEnumerationFailed;
        if (en.FailureCause == RegionEnumerationFailure.ProcessExit)
            cause = ScanIncompleteCause.RegionEnumerationFailed;

        byte[] chunk = new byte[options.ChunkSizeBytes];
        byte[] carry = new byte[MaxCarry];
        int cLen = 0;
        long? prevEnd = null;
        byte[] sBuf = new byte[V5Constants.SentinelSize];
        byte[] aBuf = new byte[V5Constants.BufferSlotSize];
        byte[] bBuf = new byte[V5Constants.BufferSlotSize];

        foreach (var reg in en.Regions)
        {
            ct.ThrowIfCancellationRequested();
            long rBase = (long)reg.BaseAddress;
            long rEnd;
            try { rEnd = checked(rBase + reg.RegionSize); }
            catch (OverflowException) { cause |= ScanIncompleteCause.RegionEnumerationFailed; cLen = 0; prevEnd = null; continue; }
            if (reg.RegionSize <= 0 || rEnd <= rBase) { cause |= ScanIncompleteCause.RegionEnumerationFailed; cLen = 0; prevEnd = null; continue; }

            if (prevEnd != rBase) cLen = 0;
            long rem = reg.RegionSize, cur = rBase;

            while (rem > 0)
            {
                ct.ThrowIfCancellationRequested();
                int cs = (int)Math.Min(rem, options.ChunkSizeBytes);
                bool contOk = true;
                try { reader.ReadExact((nint)cur, chunk, cs); }
                catch (ReaderException ex) when (ex.FailureCode == ReaderFailureCode.ReadFailure)
                { readFails++; cause |= ScanIncompleteCause.ReadFailure; cLen = 0; prevEnd = null; break; }

                bytes += cs;
                int sl = cLen + cs, limit = sl - MagicBytes.Length;

                for (int i = 0; i <= limit; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!MagicAt(carry, cLen, chunk, i)) continue;
                    raw++;
                    if (raw > options.MaxRawMatches) { cause |= ScanIncompleteCause.RawMatchLimitExceeded; return Result(); }

                    long sa = checked(cur - cLen + i);
                    if (!TryRead(reader, (nint)sa, sBuf, V5Constants.SentinelSize)) { readFails++; cause |= ScanIncompleteCause.ReadFailure; contOk = false; continue; }

                    var s = MemoryMarshal.Read<V5Sentinel>(sBuf);
                    if (!s.IsMagicValid() || s.TotalSize != V5Constants.RegionTotalSize || s.BufferSlotSize != V5Constants.BufferSlotSize) continue;
                    exact++;

                    var fa = ReadSlot(reader, checked((nint)(sa + V5Constants.BufferAOffset)), aBuf, 0, out bool rfa);
                    var fb = ReadSlot(reader, checked((nint)(sa + V5Constants.BufferBOffset)), bBuf, 1, out bool rfb);
                    readFails += (rfa ? 1 : 0) + (rfb ? 1 : 0);
                    if (rfa || rfb) { cause |= ScanIncompleteCause.ReadFailure; contOk = false; }
                    if (fa is null && fb is null) continue;

                    candidates.Add(new V5Candidate { BaseAddress = (nint)sa, Sentinel = s, FrameA = fa, FrameB = fb });
                }

                if (contOk) UpdateCarry(carry, ref cLen, chunk, cs);
                else cLen = 0;
                cur = checked(cur + cs); rem -= cs;
            }
            if (cLen > 0 && rem == 0) prevEnd = rEnd;
        }

        // Deduplicate equivalent candidates before MaxCandidates cap
        candidates = Deduplicate(candidates);
        if (candidates.Count > options.MaxCandidates)
        {
            cause |= ScanIncompleteCause.CandidateLimitExceeded;
            // Prefer freshest sequences so GC'd Lua region copies do not crowd out live ones.
            candidates = RankByFreshness(candidates, options.MaxCandidates);
        }

        return Result();

        ScanResult Result() => new(candidates.AsReadOnly(), raw, exact, bytes, en.Regions.Count,
            readFails, cause, Stopwatch.GetElapsedTime(t0), en.FailureCause, en.NativeErrorCode);
    }

    /// <summary>
    /// Keep the <paramref name="max"/> candidates with the highest session sequence
    /// (then producer frame time) so stale immutable-string copies lose to live ones.
    /// </summary>
    private static List<V5Candidate> RankByFreshness(List<V5Candidate> candidates, int max)
    {
        return candidates
            .Select(c =>
            {
                var sel = V5FrameSelector.Select(c.FrameA, c.FrameB, out var rep);
                uint seq = rep?.Header.Sequence ?? 0;
                uint prod = rep?.Header.ProducerFrameMs ?? 0;
                int valid = sel is V5SelectionResult.Selected or V5SelectionResult.Equivalent ? 1 : 0;
                return (c, valid, seq, prod);
            })
            .OrderByDescending(t => t.valid)
            .ThenByDescending(t => t.seq)
            .ThenByDescending(t => t.prod)
            .Take(max)
            .Select(t => t.c)
            .ToList();
    }

    /// <summary>
    /// Deduplicate protocol-equivalent candidates. Each candidate's identity is
    /// derived from <see cref="V5FrameSelector.Select"/>'s chosen representative,
    /// not a fixed slot. If a candidate's own slots are ambiguous or both invalid,
    /// the candidate is preserved as-is (not collapsed) so that
    /// <see cref="V5FrameSelector.SelectBest"/> can fail closed.
    /// </summary>
    private static List<V5Candidate> Deduplicate(List<V5Candidate> candidates)
    {
        var seen = new HashSet<(Guid, uint, uint, uint, uint, uint, byte, byte, ushort, uint)>();
        var result = new List<V5Candidate>(candidates.Count);
        foreach (var c in candidates)
        {
            // Use V5FrameSelector to pick the authoritative representative frame
            var sel = V5FrameSelector.Select(c.FrameA, c.FrameB, out var rep);
            // If the candidate couldn't produce a clear representative, preserve it
            // so SelectBest can fail on ambiguity.
            if (sel is V5SelectionResult.NoneValid || rep?.Provider is null)
            {
                result.Add(c);
                continue;
            }
            if (sel == V5SelectionResult.Ambiguous)
            {
                result.Add(c);
                continue;
            }

            var key = (rep.Provider.SessionId, rep.Header.Sequence, rep.Header.ProducerFrameMs,
                rep.Header.SectionsMask, rep.Header.HeartbeatIntervalMs, rep.Header.PayloadLength,
                rep.Header.ProtocolVersion, rep.Header.Flags, rep.Header.Reserved, rep.Header.Crc32);
            if (seen.Add(key)) result.Add(c);
        }
        return result;
    }

    public static V5Candidate? ValidateCandidate(IMemoryReader reader, nint addr, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        byte[] sb = new byte[V5Constants.SentinelSize];
        if (!TryRead(reader, addr, sb, V5Constants.SentinelSize)) return null;
        var s = MemoryMarshal.Read<V5Sentinel>(sb);
        if (!s.IsMagicValid() || s.TotalSize != V5Constants.RegionTotalSize || s.BufferSlotSize != V5Constants.BufferSlotSize) return null;

        ct.ThrowIfCancellationRequested();
        byte[] ab = new byte[V5Constants.BufferSlotSize], bb = new byte[V5Constants.BufferSlotSize];
        var fa = ReadSlot(reader, checked(addr + V5Constants.BufferAOffset), ab, 0, out _);
        ct.ThrowIfCancellationRequested();
        var fb = ReadSlot(reader, checked(addr + V5Constants.BufferBOffset), bb, 1, out _);
        if (fa is null && fb is null) return null;
        return new V5Candidate { BaseAddress = addr, Sentinel = s, FrameA = fa, FrameB = fb };
    }

    private static bool MagicAt(byte[] carry, int cLen, byte[] ch, int idx)
    {
        for (int j = 0; j < MagicBytes.Length; j++)
            if ((idx + j < cLen ? carry[idx + j] : ch[idx + j - cLen]) != MagicBytes[j]) return false;
        return true;
    }

    private static void UpdateCarry(byte[] c, ref int cl, byte[] ch, int cs)
    {
        if (cs >= MaxCarry) { cl = MaxCarry; Array.Copy(ch, cs - cl, c, 0, cl); return; }
        int nl = Math.Min(MaxCarry, cl + cs);
        int keep = nl - cs;
        if (keep > 0) Array.Copy(c, cl - keep, c, 0, keep);
        Array.Copy(ch, 0, c, keep, cs);
        cl = nl;
    }

    private static bool TryRead(IMemoryReader r, nint a, byte[] b, int s)
    {
        try { r.ReadExact(a, b, s); return true; }
        catch (ReaderException ex) when (ex.FailureCode == ReaderFailureCode.ReadFailure) { return false; }
    }

    private static ParsedV5Frame? ReadSlot(IMemoryReader r, nint a, byte[] b, int idx, out bool rf)
    {
        rf = false;
        try { r.ReadExact(a, b, V5Constants.BufferSlotSize); }
        catch (ReaderException ex) when (ex.FailureCode == ReaderFailureCode.ReadFailure) { rf = true; return null; }
        return V5FrameValidator.ParseValidFrame(b, idx, out _);
    }

    internal sealed record ScanResult(
        IReadOnlyList<V5Candidate> Candidates, long RawMagicMatches, long ExactSentinelMatches,
        long BytesScanned, long TotalRegions, long ReadFailures,
        ScanIncompleteCause IncompleteCause, TimeSpan Duration,
        RegionEnumerationFailure EnumFailure = RegionEnumerationFailure.None, int EnumNativeError = 0)
    {
        public bool Incomplete => IncompleteCause != ScanIncompleteCause.None;
    }
}
