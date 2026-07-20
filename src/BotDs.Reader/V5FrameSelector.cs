using BotDs.Reader.V5;

namespace BotDs.Reader;

public enum V5SelectionResult { Selected, Ambiguous, NoneValid, Equivalent }

public static class V5FrameSelector
{
    /// <summary>
    /// Select the newer frame. If frames share the same session, sequence, and producer time,
    /// checks whether the parsed frames carry equivalent complete header identity.
    /// Equivalent frames return <see cref="V5SelectionResult.Equivalent"/> and a deterministic
    /// representative. Frames that share session+sequence but differ in CRC/header produce
    /// <see cref="V5SelectionResult.Ambiguous"/>.
    /// </summary>
    public static V5SelectionResult Select(
        ParsedV5Frame? frameA, ParsedV5Frame? frameB, out ParsedV5Frame? selected)
    {
        selected = null;
        if (frameA is null && frameB is null) return V5SelectionResult.NoneValid;
        if (frameA is null) { selected = frameB; return V5SelectionResult.Selected; }
        if (frameB is null) { selected = frameA; return V5SelectionResult.Selected; }
        if (frameA.Provider is null || frameB.Provider is null) return V5SelectionResult.Ambiguous;

        if (frameA.Provider.SessionId == frameB.Provider.SessionId)
        {
            uint sa = frameA.Header.Sequence, sb = frameB.Header.Sequence;
            if (sa == sb)
                return ResolveSameSequence(frameA, frameB, out selected);

            if (SessionTracker.IsAmbiguous(sb, sa)) return V5SelectionResult.Ambiguous;
            selected = SessionTracker.IsAfter(sb, sa) ? frameB : frameA;
            return V5SelectionResult.Selected;
        }

        int order = CompareProducerFrameTime(frameA.Header.ProducerFrameMs, frameB.Header.ProducerFrameMs);
        if (order == 0) return V5SelectionResult.Ambiguous;
        selected = order > 0 ? frameA : frameB;
        return V5SelectionResult.Selected;
    }

    /// <summary>
    /// Two frames from the same session with the same sequence number.
    /// If every non-sequence header field matches,
    /// they are equivalent — return the lower-BufferIndex frame as representative.
    /// Otherwise they conflict and are ambiguous.
    /// </summary>
    private static V5SelectionResult ResolveSameSequence(
        ParsedV5Frame frameA, ParsedV5Frame frameB, out ParsedV5Frame? selected)
    {
        selected = null;
        V5BufferHeader a = frameA.Header;
        V5BufferHeader b = frameB.Header;
        bool sameIdentity = a.ProducerFrameMs == b.ProducerFrameMs
            && a.SectionsMask == b.SectionsMask
            && a.HeartbeatIntervalMs == b.HeartbeatIntervalMs
            && a.PayloadLength == b.PayloadLength
            && a.ProtocolVersion == b.ProtocolVersion
            && a.Flags == b.Flags
            && a.Reserved == b.Reserved
            && a.Crc32 == b.Crc32;

        if (sameIdentity)
        {
            selected = frameA.BufferIndex <= frameB.BufferIndex ? frameA : frameB;
            return V5SelectionResult.Equivalent;
        }
        return V5SelectionResult.Ambiguous;
    }

    /// <summary>
    /// SelectBest first deduplicates candidates with equivalent complete header identity,
    /// then finds a unique candidate that is strictly newer than all others.
    /// Returns Ambiguous if two distinct yet conflicting equal-order candidates exist.
    /// </summary>
    internal static V5SelectionResult SelectBest(
        IReadOnlyList<V5Candidate> candidates, out V5Candidate? selected)
    {
        selected = null;
        var list = new List<(V5Candidate, ParsedV5Frame)>();

        foreach (var c in candidates)
        {
            if (!c.IsValid) continue;
            var own = Select(c.FrameA, c.FrameB, out var f);
            if (own is V5SelectionResult.Ambiguous) return V5SelectionResult.Ambiguous;
            if ((own == V5SelectionResult.Selected || own == V5SelectionResult.Equivalent) && f?.Provider is not null)
                list.Add((c, f));
        }

        if (list.Count == 0) return V5SelectionResult.NoneValid;

        // Deduplicate equivalent candidates
        var deduped = new List<(V5Candidate, ParsedV5Frame)>();
        foreach (var (c, f) in list)
        {
            bool isDup = false;
            foreach (var (c2, f2) in deduped)
            {
                var cmp = Select(f, f2, out _);
                if (cmp == V5SelectionResult.Equivalent) { isDup = true; break; }
            }
            if (!isDup) deduped.Add((c, f));
        }
        if (deduped.Count == 0) return V5SelectionResult.NoneValid;
        if (deduped.Count == 1) { selected = deduped[0].Item1; return V5SelectionResult.Selected; }

        // Find a unique strict-maximum
        int winner = -1;
        for (int i = 0; i < deduped.Count; i++)
        {
            bool newerThanAll = true;
            for (int j = 0; j < deduped.Count; j++)
            {
                if (i == j) continue;
                if (CompareStrict(deduped[i].Item2, deduped[j].Item2) != 1) { newerThanAll = false; break; }
            }
            if (newerThanAll)
            {
                if (winner >= 0) return V5SelectionResult.Ambiguous;
                winner = i;
            }
        }
        if (winner < 0) return V5SelectionResult.Ambiguous;
        selected = deduped[winner].Item1;
        return V5SelectionResult.Selected;
    }

    private static int CompareStrict(ParsedV5Frame left, ParsedV5Frame right)
    {
        if (left.Provider is null || right.Provider is null) return 0;
        if (left.Provider.SessionId == right.Provider.SessionId)
        {
            uint d = unchecked(left.Header.Sequence - right.Header.Sequence);
            if (d == 0) return 0; // same seq — use Equivalence check separately
            if (d == 0x80000000) return 0;
            return d < 0x80000000 ? 1 : -1;
        }
        return CompareProducerFrameTime(left.Header.ProducerFrameMs, right.Header.ProducerFrameMs);
    }

    private static int CompareProducerFrameTime(uint a, uint b)
    {
        uint d = unchecked(a - b);
        if (d is 0 or 0x80000000) return 0;
        return d < 0x80000000 ? 1 : -1;
    }
}

internal static class V5FrameValidator
{
    internal static ParsedV5Frame? ParseValidFrame(ReadOnlySpan<byte> buf, int idx, out string failure)
    {
        V5ParseResult r = V5Parser.ParseAndValidate(buf, idx);
        if (!r.IsValid || r.Frame is null)
        {
            failure = r.FailureDetail is { Length: > 0 } ? $"{r.Failure}: {r.FailureDetail}" : r.Failure.ToString();
            return null;
        }
        if (r.Frame.Provider is null) { failure = "ProviderInfo required"; return null; }
        if (r.Frame.Header.ProducerFrameMs != r.Frame.Provider.ProducerFrameMs) { failure = "ProducerFrameMs mismatch"; return null; }
        failure = "";
        return r.Frame;
    }
}
