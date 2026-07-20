using System.Runtime.InteropServices;
using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;

namespace BotDs.Tests;

/// <summary>
/// Adversarial tests for the V5 scanner pipeline. Every test targets a specific
/// contract or failure mode derived from the scanner's observable behavior.
/// </summary>
public sealed class ScannerAdversarialTests
{
    // ────────────────────────────────────────────────────────────────
    // 1. True magic split at chunk boundary
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Magic_SplitExactlyAtChunkBoundary_IsFound()
    {
        // "BotDsV05" = 8 bytes. Place sentinel so magic straddles two 256-byte chunks.
        var cat = new FakeMemoryCatalog();
        nint regionBase = 0x2000000;
        byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, Guid.NewGuid());

        // The first 4 magic bytes end the first chunk and the last 4 begin the second.
        int paddingBefore = 252;
        byte[] region = new byte[paddingBefore + V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, paddingBefore);
        slotA.CopyTo(region, paddingBefore + V5Constants.BufferAOffset);
        slotB.CopyTo(region, paddingBefore + V5Constants.BufferBOffset);
        cat.AddPage(regionBase, region);

        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { ChunkSizeBytes = 256 };
        var result = V5SentinelScanner.Scan(r, opts);

        Assert.Single(result.Candidates);
        Assert.Equal(regionBase + paddingBefore, result.Candidates[0].BaseAddress);
        Assert.False(result.Incomplete);
    }

    [Theory]
    [InlineData(1)]   // split after 1st magic byte
    [InlineData(7)]   // split after 7th magic byte
    public void Magic_SplitAtBoundaryOffsets_Found(int bytesBeforeBoundary)
    {
        var cat = new FakeMemoryCatalog();
        nint regionBase = 0x3000000;
        byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, Guid.NewGuid());

        int sentinelOffset = 256 - bytesBeforeBoundary;
        byte[] region = new byte[sentinelOffset + V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, sentinelOffset);
        slotA.CopyTo(region, sentinelOffset + V5Constants.BufferAOffset);
        slotB.CopyTo(region, sentinelOffset + V5Constants.BufferBOffset);
        cat.AddPage(regionBase, region);

        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { ChunkSizeBytes = 256 };
        var result = V5SentinelScanner.Scan(r, opts);

        Assert.Single(result.Candidates);
        Assert.False(result.Incomplete);
    }

    // ────────────────────────────────────────────────────────────────
    // 2. Carry-over reset across region gaps
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void CarryOver_ResetAcrossRegionGap_NoCrossRegionMatch()
    {
        var cat = new FakeMemoryCatalog();
        nint region1Base = 0x4000000;
        byte[] magic = "BotDsV05"u8.ToArray();

        byte[] region1 = new byte[512];
        Array.Copy(magic, 0, region1, 508, 4);
        cat.AddPage(region1Base, region1);

        byte[] region2 = new byte[V5Constants.RegionTotalSize];
        Array.Copy(magic, 4, region2, 4, 4);
        cat.AddPage(0x5000000, region2);

        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { ChunkSizeBytes = 512 };
        var result = V5SentinelScanner.Scan(r, opts);

        Assert.Empty(result.Candidates);
        Assert.Equal(0, result.RawMagicMatches);
        Assert.Equal(0, result.ExactSentinelMatches);
    }

    // ────────────────────────────────────────────────────────────────
    // 3. Incomplete enumeration → fails closed (Incomplete=true)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void IncompleteEnumeration_SetsIncompleteFlag()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        cat.MarkEnumerationIncomplete();
        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.True(result.Incomplete);
        Assert.Equal(ScanIncompleteCause.RegionEnumerationFailed, result.IncompleteCause);
    }

    [Fact]
    public void IncompleteEnumeration_ServiceReturnsQueryFailure()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        cat.MarkEnumerationIncomplete();
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);
        var result = svc.Read();
        Assert.False(result.IsUsable);
        Assert.Equal(ReaderFailureCode.QueryFailure, result.FailureCode);
    }

    // ────────────────────────────────────────────────────────────────
    // 4. Raw match limit → fails closed
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxRawMatches_Breached_ScanIncomplete()
    {
        var cat = new FakeMemoryCatalog();
        // Place 50 decoy "BotDsV05" magics across pages (bad sentinel sizes)
        for (int i = 0; i < 50; i++)
        {
            var sentinel = ScannerTestHelpers.BuildBadSizeSentinel();
            var page = new byte[V5Constants.SentinelSize + 20];
            sentinel.CopyTo(page, i % 8);
            cat.AddPage(0x10000 + (i * 0x1000), page);
        }
        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { MaxRawMatches = 10 };
        var result = V5SentinelScanner.Scan(r, opts);

        Assert.True(result.Incomplete);
        Assert.Equal(11, result.RawMagicMatches);
        Assert.Equal(0, result.ExactSentinelMatches);
        Assert.Equal(ScanIncompleteCause.RawMatchLimitExceeded, result.IncompleteCause);
    }

    // ────────────────────────────────────────────────────────────────
    // 5. Candidate limit → fails closed
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxCandidates_Breached_ScanIncomplete()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 10; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x10000 + (i * 0x100000),
                sessionId: Guid.NewGuid(), seqA: (uint)(i * 2 + 1), seqB: (uint)(i * 2 + 2));
        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { MaxCandidates = 2 };
        var result = V5SentinelScanner.Scan(r, opts);

        Assert.True(result.Incomplete);
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(result.ExactSentinelMatches >= 3); // all sentinels counted before dedup+cap
        Assert.Equal(ScanIncompleteCause.CandidateLimitExceeded, result.IncompleteCause);
    }

    [Fact]
    public void Service_Read_MaxCandidatesReached_ReturnsCandidateLimitExceeded()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 5; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x10000 + (i * 0x100000),
                sessionId: Guid.NewGuid(), seqA: (uint)(i * 2 + 1), seqB: (uint)(i * 2 + 2));
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var opts = new SentinelScannerOptions { MaxCandidates = 1 };
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact, scannerOptions: opts);
        var result = svc.Read();
        Assert.False(result.IsUsable);
        Assert.Equal(ReaderFailureCode.CandidateLimitExceeded, result.FailureCode);
        Assert.Equal(ProviderHealth.Faulted, result.ReadResult.TransportHealth);
    }

    // ────────────────────────────────────────────────────────────────
    // 6. SentinelScannerOptions validation
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    [InlineData(64 * 1024 * 1024 + 1)]
    public void SentinelScannerOptions_InvalidChunkSize_Throws(int chunk)
    {
        var opts = new SentinelScannerOptions { ChunkSizeBytes = chunk };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_001)]
    public void SentinelScannerOptions_InvalidMaxRawMatches_Throws(int max)
    {
        var opts = new SentinelScannerOptions { MaxRawMatches = max };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_001)]
    public void SentinelScannerOptions_InvalidMaxCandidates_Throws(int max)
    {
        var opts = new SentinelScannerOptions { MaxCandidates = max };
        Assert.Throws<ArgumentOutOfRangeException>(() => opts.Validate());
    }

    // ────────────────────────────────────────────────────────────────
    // 7. Invalid PID / path / wildcard process names
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Service_InvalidPid_ThrowsOnConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new V5ScannerService(new ProcessSelector { ProcessId = 0 }, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentException>(() =>
            new V5ScannerService(new ProcessSelector { ProcessId = -5 }, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ProcessSelector_PathInName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProcessSelector { ProcessName = @"C:\Windows\notepad.exe" });
        Assert.Throws<ArgumentException>(() =>
            new ProcessSelector { ProcessName = "dir/name" });
    }

    [Fact]
    public void ProcessSelector_WildcardInName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProcessSelector { ProcessName = "rift*" });
        Assert.Throws<ArgumentException>(() =>
            new ProcessSelector { ProcessName = "ri?t" });
    }

    // ────────────────────────────────────────────────────────────────
    // 8. Missing ProviderInfo → rejected
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_SentinelWithoutProviderInfo_RejectedAsNoCandidate()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithoutProvider(1);
        var slotB = ScannerTestHelpers.BuildSlotWithoutProvider(2);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.RawMagicMatches);
        Assert.Equal(1, result.ExactSentinelMatches);
    }

    [Fact]
    public void ValidateCandidate_NoProvider_ReturnsNull()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithoutProvider(1);
        var slotB = ScannerTestHelpers.BuildSlotWithoutProvider(2);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var c = V5SentinelScanner.ValidateCandidate(r, 0x1000000);
        Assert.Null(c);
    }

    // ────────────────────────────────────────────────────────────────
    // 9. Header/provider ProducerFrameMs mismatch → rejected
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_BothSlotsMismatchedProducerFrame_Rejected()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(1, sid, 100, 200);
        var slotB = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(2, sid, 300, 400);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        // Both slots have mismatched header/provider → both null → no candidate
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_OneSlotMismatched_OtherAccepted()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(1, sid, 100, 200);
        var slotB = ScannerTestHelpers.BuildSlot(2, sid, 500);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Single(result.Candidates);
        Assert.Null(result.Candidates[0].FrameA); // rejected mismatch
        Assert.NotNull(result.Candidates[0].FrameB); // accepted
    }

    [Fact]
    public void ValidateCandidate_MismatchedProducerFrame_Rejected()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(1, sid, 100, 200);
        var slotB = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(2, sid, 300, 400);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var c = V5SentinelScanner.ValidateCandidate(r, 0x1000000);
        Assert.Null(c);
    }

    // ────────────────────────────────────────────────────────────────
    // 10. Mixed one-slot / two-slot candidate selection order independence
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SelectBest_MixedSlots_OrderIndependent()
    {
        var sid = Guid.NewGuid();
        // Candidate A: one slot, seq=100
        var cA = new V5Candidate
        {
            BaseAddress = 0xA000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 100),
            FrameB = null,
        };
        // Candidate B: two slots, seq=50 and seq=200
        var cB = new V5Candidate
        {
            BaseAddress = 0xB000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 50),
            FrameB = MkFrame(sid, 200),
        };
        // Candidate C: one slot, seq=150
        var cC = new V5Candidate
        {
            BaseAddress = 0xC000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 150),
            FrameB = null,
        };

        // B's best is seq 200 (from FrameB). Winner regardless of input order.
        var orders = new[]
        {
            new[] { cA, cB, cC },
            new[] { cC, cA, cB },
            new[] { cB, cC, cA },
            new[] { cA, cC, cB },
        };
        foreach (var order in orders)
        {
            var r = V5FrameSelector.SelectBest(order, out var best);
            Assert.Equal(V5SelectionResult.Selected, r);
            Assert.NotNull(best);
            Assert.Equal(0xB000, best.BaseAddress);
            // B's best frame is FrameB with seq 200
            var sel = V5FrameSelector.Select(best.FrameA, best.FrameB, out var bestFrame);
            Assert.Equal(V5SelectionResult.Selected, sel);
            Assert.Equal(200u, bestFrame!.Header.Sequence);
        }
    }

    [Fact]
    public void SelectBest_AllOneSlot_OrderIndependent()
    {
        var sid = Guid.NewGuid();
        var c1 = new V5Candidate
        {
            BaseAddress = 0x1000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 10),
            FrameB = null,
        };
        var c2 = new V5Candidate
        {
            BaseAddress = 0x2000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 50),
            FrameB = null,
        };
        var c3 = new V5Candidate
        {
            BaseAddress = 0x3000,
            Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize },
            FrameA = MkFrame(sid, 30),
            FrameB = null,
        };

        // Reverse order
        var r1 = V5FrameSelector.SelectBest(new[] { c3, c2, c1 }, out var b1);
        var r2 = V5FrameSelector.SelectBest(new[] { c1, c3, c2 }, out var b2);
        Assert.Equal(V5SelectionResult.Selected, r1);
        Assert.Equal(V5SelectionResult.Selected, r2);
        Assert.Equal(b1!.BaseAddress, b2!.BaseAddress);
        Assert.Equal(0x2000, b1.BaseAddress);
    }

    // ────────────────────────────────────────────────────────────────
    // 11. Stale cache → relocation to newer address
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void StaleCache_RelocatesAndFindsNewSentinel()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        nint addr1 = 0x1000000;
        nint addr2 = 0x2000000;

        ScannerTestHelpers.PlaceV5Region(cat, addr1, sessionId: sid, seqA: 1, seqB: 2);

        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(100), readerFactory: fact, timeProvider: tp);

        // First read caches addr1
        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(1, svc.AttachmentGeneration);

        // Advance past max age → stale
        tp.Advance(TimeSpan.FromMilliseconds(500));

        // Move sentinel to addr2 (old one is now gone)
        // First remove the old one
        cat.ModifyPage(addr1, 0, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
        // Place at new location
        ScannerTestHelpers.PlaceV5Region(cat, addr2, sessionId: sid, seqA: 3, seqB: 4);

        // After relocation, frame may be Degraded (sequence gap from 2→4) but the
        // sentinel was found and read successfully. Verify relocation scan happened.
        var r2 = svc.Read();
        Assert.Equal(2, r2.Metrics.FullScanCount); // initial + relocation scan
        Assert.NotNull(r2.ReadResult.Frame); // frame was successfully read
        Assert.Equal(ProviderHealth.Degraded, r2.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.ContinuityDegraded, r2.FailureCode);
        // The frame should have the sequences from addr2
        Assert.True(r2.ReadResult.Frame!.Header.Sequence == 3 || r2.ReadResult.Frame.Header.Sequence == 4);
    }

    // ────────────────────────────────────────────────────────────────
    // 12. Process exit clears attachment
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessExit_AttachmentPidCleared()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(42, svc.AttachmentPid);

        // Process dies
        fact.GetLastReader(42)?.Kill();
        fact.KillProcess(42);

        var r2 = svc.Read();
        Assert.False(r2.IsUsable);
        Assert.Equal(ReaderFailureCode.ProcessExit, r2.FailureCode);
        Assert.Equal(0, svc.AttachmentPid);
    }

    // ────────────────────────────────────────────────────────────────
    // 13. Cancellation propagates
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancellation_ReaderThrowsOperationCanceledException()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => svc.Read(cts.Token));
    }

    [Fact]
    public void CancellationToken_IsPassedIntoRegionEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = new CancellingEnumerationReader(cancellation);

        Assert.Throws<OperationCanceledException>(() =>
            V5SentinelScanner.Scan(reader, new SentinelScannerOptions(), cancellation.Token));
        Assert.True(reader.ReceivedCancelableToken);
    }

    // ────────────────────────────────────────────────────────────────
    // 14. Failure details must not expose hex addresses or paths
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_StripsHexAddresses()
    {
        var input = "Read failed at 0x7FFE00000000 (size 4096)";
        var result = V5ScannerService.Sanitize(input);
        Assert.DoesNotContain("7FFE0000", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(addr)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_StripsPathsWithSpacesAndSessionIdentifiers()
    {
        var input = "Session 11111111-1111-1111-1111-111111111111 cannot open C:\\Program Files\\BotDs\\reader.dll";
        var result = V5ScannerService.Sanitize(input);
        Assert.DoesNotContain("C:\\", result, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111", result, StringComparison.Ordinal);
        Assert.Contains("(path)", result, StringComparison.Ordinal);
        Assert.Contains("(id)", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_TruncatesLongMessages()
    {
        string longMsg = new string('X', 500);
        var result = V5ScannerService.Sanitize(longMsg);
        Assert.True(result.Length <= 200);
    }

    [Fact]
    public void Sanitize_EmptyAndNull_ReturnEmpty()
    {
        Assert.Equal("", V5ScannerService.Sanitize(null));
        Assert.Equal("", V5ScannerService.Sanitize(""));
    }

    // ────────────────────────────────────────────────────────────────
    // 15. Dispose/read behavior
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_PreventsSubsequentReads()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        Assert.True(svc.Read().IsUsable);
        svc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => svc.Read());
    }

    [Fact]
    public void Dispose_DoubleDispose_IsIdempotent()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        Assert.True(svc.Read().IsUsable);
        svc.Dispose();
        svc.Dispose(); // should not throw
        Assert.Throws<ObjectDisposedException>(() => svc.Read());
    }

    [Fact]
    public void Dispose_ReaderIsReleased()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        Assert.True(svc.Read().IsUsable);
        var reader = fact.GetLastReader(42);
        Assert.NotNull(reader);
        Assert.True(reader.IsAlive);

        svc.Dispose();
        Assert.False(reader.IsAlive);
        Assert.Equal(0, svc.AttachmentPid);
    }

    // ────────────────────────────────────────────────────────────────
    // 16. Bad sentinel magic → rejected
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_BadMagic_Skipped()
    {
        var cat = new FakeMemoryCatalog();
        // Page with "BotDsV04" (wrong version)
        byte[] page = new byte[V5Constants.SentinelSize + 40];
        "BotDsV04"u8.CopyTo(page);
        cat.AddPage(0x1000000, page);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_BadTotalSize_Skipped()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildBadSizeSentinel();
        byte[] page = new byte[V5Constants.SentinelSize + 40];
        sentinel.CopyTo(page, 0);
        cat.AddPage(0x1000000, page);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
        Assert.Equal(1, result.RawMagicMatches);
        Assert.Equal(0, result.ExactSentinelMatches);
    }

    [Fact]
    public void Scan_RandomBytes_NoFalsePositives()
    {
        var cat = new FakeMemoryCatalog();
        var rng = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            byte[] page = new byte[0x1000];
            rng.NextBytes(page);
            cat.AddPage(0x1000000 + (i * 0x1000), page);
        }

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
    }

    // ────────────────────────────────────────────────────────────────
    // 17. Read failure mid-region → marks incomplete
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReadFailure_MidRegion_MarksIncomplete()
    {
        // Custom reader: reports one large region, but reads fail after first chunk.
        // The sentinel + one valid slot fit within the first chunk so we get a candidate,
        // but the scanner must continue scanning the rest of the region and encounter a failure.
        nint regionBase = 0x1000000;
        long regionSize = V5Constants.RegionTotalSize * 2; // region is twice the sentinel layout
        int chunkSize = V5Constants.RegionTotalSize;       // one full sentinel layout per chunk

        // Only the first chunk is readable; contains a valid V5 region
        byte[] readableChunk = new byte[chunkSize];
        PlaceV5Region_Into(readableChunk, 0,
            sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);

        var reader = new PartialReadReader(regionBase, regionSize, chunkSize, readableChunk);
        var result = V5SentinelScanner.Scan(reader, new SentinelScannerOptions { ChunkSizeBytes = chunkSize });
        Assert.Single(result.Candidates);
        Assert.True(result.ReadFailures > 0, "Second chunk read failure must be recorded");
        Assert.True(result.Incomplete, "Read failure in second chunk must set Incomplete");
        Assert.Equal(ScanIncompleteCause.ReadFailure, result.IncompleteCause);
    }

    [Fact]
    public void ReadFailure_MidRegion_ServiceReturnsFaultedReadFailureWithoutRankingPartialSet()
    {
        nint regionBase = 0x1000000;
        int chunkSize = V5Constants.RegionTotalSize;
        byte[] readableChunk = new byte[chunkSize];
        PlaceV5Region_Into(readableChunk, 0, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var reader = new PartialReadReader(
            regionBase,
            V5Constants.RegionTotalSize * 2L,
            chunkSize,
            readableChunk);
        using var service = new V5ScannerService(
            new ProcessSelector { ProcessId = reader.ProcessId },
            TimeSpan.FromSeconds(5),
            readerFactory: new SingleReaderFactory(reader),
            scannerOptions: new SentinelScannerOptions { ChunkSizeBytes = chunkSize });

        ScannerReadResult result = service.Read();

        Assert.False(result.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, result.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.ReadFailure, result.FailureCode);
        Assert.Equal(1, result.Metrics.ValidCandidatesFound);
        Assert.Equal(0, result.Metrics.CacheHitCount);
    }

    /// <summary>Helper: IMemoryReader that reports one region but only allows reading the first chunk.</summary>
    private sealed class PartialReadReader : IMemoryReader
    {
        private readonly nint _base;
        private readonly long _size;
        private readonly int _chunkSize;
        private readonly byte[] _firstChunk;
        private bool _alive = true;

        public PartialReadReader(nint regionBase, long regionSize, int chunkSize, byte[] firstChunk)
        {
            _base = regionBase;
            _size = regionSize;
            _chunkSize = chunkSize;
            _firstChunk = firstChunk;
        }

        public int ProcessId => 99;
        public bool IsAlive => _alive;
        public bool CheckLiveness() => _alive;
        public void Dispose() => _alive = false;

        public RegionEnumerationResult QueryReadableRegions(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new([new MemoryRegion(_base, _size)], IsComplete: true);
        }

        public void ReadExact(nint address, byte[] buffer, int size)
        {
            if (!_alive) throw new ReaderException(ReaderFailureCode.ProcessExit, "dead");
            long offset = (long)(address - _base);
            // Only first chunk is readable
            if (offset >= 0 && offset + size <= _chunkSize)
                Array.Copy(_firstChunk, (int)offset, buffer, 0, size);
            else
                throw new ReaderException(ReaderFailureCode.ReadFailure, "Read failed at (addr)");
        }
    }

    private sealed class SingleReaderFactory(IMemoryReader reader) : IMemoryReaderFactory
    {
        public IMemoryReader Open(int processId, string? expectedName = null) => reader;
    }

    private sealed class CancellingEnumerationReader(CancellationTokenSource source) : IMemoryReader
    {
        public int ProcessId => 100;
        public bool IsAlive => true;
        public bool ReceivedCancelableToken { get; private set; }
        public bool CheckLiveness() => true;
        public void ReadExact(nint address, byte[] buffer, int size) =>
            throw new InvalidOperationException("Enumeration should cancel before reads.");

        public RegionEnumerationResult QueryReadableRegions(CancellationToken cancellationToken = default)
        {
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return new([], true);
        }

        public void Dispose()
        {
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 18. Corrupt CRC on both slots → no candidate
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_BothSlotsCorruptCRC_Rejected()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();

        // Build valid-looking slots but then corrupt CRC
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, Guid.NewGuid());
        slotA[V5Constants.HdrCrc32Offset] ^= 0xFF; // corrupt CRC
        slotB[V5Constants.HdrCrc32Offset] ^= 0xFF;

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
    }

    // ────────────────────────────────────────────────────────────────
    // 19. Corrupt CRC on one slot, valid on other → single candidate
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_OneSlotCorruptCRC_OtherValid_SingleCandidate()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, sid);
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, sid);
        slotA[V5Constants.HdrCrc32Offset] ^= 0xFF; // corrupt A

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Single(result.Candidates);
        Assert.Null(result.Candidates[0].FrameA);  // corrupted
        Assert.NotNull(result.Candidates[0].FrameB); // valid
    }

    // ────────────────────────────────────────────────────────────────
    // 20. ValidateCandidate at bad address → null
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ValidateCandidate_AddressNotInMemory_ReturnsNull()
    {
        var cat = new FakeMemoryCatalog();
        // No pages at 0xDEAD0000
        var r = new FakeMemoryReader(cat, 1);
        var c = V5SentinelScanner.ValidateCandidate(r, unchecked((nint)0xDEAD0000));
        Assert.Null(c);
    }

    // ────────────────────────────────────────────────────────────────
    // 21. Metrics accumulate correctly across reads
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Metrics_MultipleReads_AccumulateCorrectly()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5),
            readerFactory: fact);

        svc.Read();
        svc.Read();
        svc.Read();

        var m = svc.Metrics;
        Assert.Equal(1, m.FullScanCount);           // only one full scan
        Assert.Equal(2, m.CacheHitCount);            // two cache hits
        Assert.Equal(0, m.ReadCycleFailures);
    }

    // ────────────────────────────────────────────────────────────────
    // 22. Empty region list → no sentinel
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_EmptyRegionList_NoCandidates()
    {
        var cat = new FakeMemoryCatalog(); // no pages
        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
        Assert.False(result.Incomplete);
        Assert.Equal(0, result.BytesScanned);
        Assert.Equal(0, result.TotalRegions);
    }

    // ────────────────────────────────────────────────────────────────
    // 23. Disposed reader during scan → read failure
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ReaderDisposed_DuringScan_PreservesProcessExitException()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var r = new FakeMemoryReader(cat, 1);
        r.Dispose(); // kill the reader first

        ReaderException exception = Assert.Throws<ReaderException>(
            () => V5SentinelScanner.Scan(r, new SentinelScannerOptions()));
        Assert.Equal(ReaderFailureCode.ProcessExit, exception.FailureCode);
    }

    // ────────────────────────────────────────────────────────────────
    // 24. Very large region with tiny chunk → finds sentinel efficiently
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void TinyChunk_LargeRegion_FindsCandidate()
    {
        var cat = new FakeMemoryCatalog();
        nint regionBase = 0x1000000;
        // 1MB region
        byte[] region = new byte[1_048_576];
        byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, Guid.NewGuid());

        int offset = 900_000; // sentinel near the end
        sentinel.CopyTo(region, offset);
        slotA.CopyTo(region, offset + V5Constants.BufferAOffset);
        slotB.CopyTo(region, offset + V5Constants.BufferBOffset);
        cat.AddPage(regionBase, region);

        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { ChunkSizeBytes = 512 };
        var result = V5SentinelScanner.Scan(r, opts);
        Assert.Single(result.Candidates);
        Assert.Equal(regionBase + offset, result.Candidates[0].BaseAddress);
        Assert.False(result.Incomplete);
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static ParsedV5Frame MkFrame(Guid sid, uint seq, uint producerMs = 0)
    {
        return new ParsedV5Frame(
            new V5BufferHeader { Sequence = seq, ProducerFrameMs = producerMs, ProtocolVersion = 5 },
            new ParsedProviderInfo(sid, producerMs, 500, "t", 1),
            null, null, [], [], [], 0);
    }

    private static ParsedV5Frame MkFrameWithMaskCrc(Guid sid, uint seq, uint mask, uint crc, uint producerMs = 0)
    {
        return new ParsedV5Frame(
            new V5BufferHeader { Sequence = seq, ProducerFrameMs = producerMs, SectionsMask = mask, Crc32 = crc, ProtocolVersion = 5 },
            new ParsedProviderInfo(sid, producerMs, 500, "t", 1),
            null, null, [], [], [], 0);
    }

    /// <summary>Place a complete V5 region layout into an existing byte array at the given offset.</summary>
    private static void PlaceV5Region_Into(byte[] target, int offset,
        Guid? sessionId = null, uint seqA = 1, uint seqB = 2)
    {
        Guid sid = sessionId ?? Guid.NewGuid();
        byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] bufferA = ScannerTestHelpers.BuildSlot(seqA, sid);
        byte[] bufferB = ScannerTestHelpers.BuildSlot(seqB, sid);
        sentinel.CopyTo(target, offset);
        bufferA.CopyTo(target, offset + V5Constants.BufferAOffset);
        bufferB.CopyTo(target, offset + V5Constants.BufferBOffset);
    }

    // ────────────────────────────────────────────────────────────────
    // 16. Equivalent candidate deduplication
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deduplication_MultipleIdenticalCopies_CountAsOne()
    {
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x1000000 + (i * 0x100000),
                sessionId: sid, seqA: 1, seqB: 2);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions { MaxCandidates = 3 });
        Assert.False(result.Incomplete);
        // All 5 are protocol-equivalent → deduped to 1
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Deduplication_EquivalentPlusNewer_SelectsNewer()
    {
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();
        // Three identical copies
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: sid, seqA: 1, seqB: 2);
        ScannerTestHelpers.PlaceV5Region(cat, 0x2000000, sessionId: sid, seqA: 1, seqB: 2);
        ScannerTestHelpers.PlaceV5Region(cat, 0x3000000, sessionId: sid, seqA: 1, seqB: 2);
        // One newer allocation
        ScannerTestHelpers.PlaceV5Region(cat, 0x4000000, sessionId: sid, seqA: 3, seqB: 4);

        var r = new FakeMemoryReader(cat, 1);
        var svc = new V5ScannerService(new ProcessSelector { ProcessId = 1 },
            TimeSpan.FromSeconds(5), readerFactory: new SingleReaderFactory(r));

        var res = svc.Read();
        Assert.True(res.IsUsable);
        Assert.Equal(4u, res.Frame!.Header.Sequence);
    }

    [Fact]
    public void Deduplication_DifferentSelectedFrames_NotCollapsed()
    {
        // Regression: candidate1 A=seq1/B=seq2, candidate2 A=seq1/B=seq4.
        // Selected frames are seq2 vs seq4 — must NOT be deduplicated, and
        // seq4 must win regardless of scan order.
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();

        // candidate1: seqA=1, seqB=2 → selected is seq2
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: sid, seqA: 1, seqB: 2);
        // candidate2: seqA=1, seqB=4 → selected is seq4
        ScannerTestHelpers.PlaceV5Region(cat, 0x2000000, sessionId: sid, seqA: 1, seqB: 4);

        var r = new FakeMemoryReader(cat, 1);
        var svc = new V5ScannerService(new ProcessSelector { ProcessId = 1 },
            TimeSpan.FromSeconds(5), readerFactory: new SingleReaderFactory(r));

        var res = svc.Read();
        Assert.True(res.IsUsable);
        // Must select seq4 — the newer frame
        Assert.Equal(4u, res.Frame!.Header.Sequence);
    }

    [Fact]
    public void SelectBest_ConflictingEqualOrder_ReturnsAmbiguous()
    {
        Guid sa = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid sb = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var c1 = new V5Candidate { BaseAddress = 0x1000, Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize }, FrameA = MkFrame(sa, 5, 100), FrameB = null };
        var c2 = new V5Candidate { BaseAddress = 0x2000, Sentinel = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize }, FrameA = null, FrameB = MkFrame(sb, 1, 100) };
        // Equal producer time, different sessions → ambiguous
        var r = V5FrameSelector.SelectBest(new[] { c1, c2 }, out _);
        Assert.Equal(V5SelectionResult.Ambiguous, r);
    }

    [Fact]
    public void Select_SameSessionSameSeq_DifferentCrc_Ambiguous()
    {
        Guid s = Guid.NewGuid();
        var fa = MkFrameWithMaskCrc(s, 42, 0x01, 0xAAAAAAAA, 0);
        var fb = MkFrameWithMaskCrc(s, 42, 0x01, 0xBBBBBBBB, 0);
        var r = V5FrameSelector.Select(fa, fb, out _);
        Assert.Equal(V5SelectionResult.Ambiguous, r);
    }

    [Fact]
    public void Select_SameSessionSameSeq_SameIdentity_Equivalent()
    {
        Guid s = Guid.NewGuid();
        var fa = MkFrameWithMaskCrc(s, 42, 0x01, 0xCAFECAFE, 0);
        var fb = MkFrameWithMaskCrc(s, 42, 0x01, 0xCAFECAFE, 0);
        var r = V5FrameSelector.Select(fa, fb, out var sel);
        Assert.Equal(V5SelectionResult.Equivalent, r);
        Assert.NotNull(sel);
        Assert.Equal(42u, sel!.Header.Sequence);
    }

    [Fact]
    public void StableReader_SequenceDecrement_MapsToSequenceDiscontinuity()
    {
        var tp = new ControllableTimeProvider();
        var reader = new StableReader(TimeSpan.FromSeconds(5), tp);
        var sid = Guid.NewGuid();
        byte[] buf1 = new byte[V5Constants.BufferSlotSize];
        byte[] buf2 = new byte[V5Constants.BufferSlotSize];
        byte[] empty = new byte[V5Constants.BufferSlotSize];
        ScannerTestHelpers.FillSlot(buf1, 100, sid);
        ScannerTestHelpers.FillSlot(buf2, 100, sid);

        Assert.True(reader.Read(s => buf1.CopyTo(s), s => empty.CopyTo(s), DateTimeOffset.UtcNow).IsUsable);
        Array.Clear(buf1);
        ScannerTestHelpers.FillSlot(buf2, 80, sid); // same session, sequence decrement from 100→80
        var r = reader.Read(s => buf2.CopyTo(s), s => buf1.CopyTo(s), DateTimeOffset.UtcNow);
        Assert.False(r.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, r.TransportHealth);
        Assert.Equal(ContinuityResult.SequenceDecrement, r.Continuity);
    }

    // ────────────────────────────────────────────────────────────────
    // 17. Factory name binding
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_Open_ReceivesExpectedName_WhenPidAndName()
    {
        var factory = new NameCapturingFactory();
        factory.Register(42);
        var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42, ProcessName = "test.exe" },
            TimeSpan.FromSeconds(5), readerFactory: factory);
        // This should call factory.Open(42, "test.exe")
        try { svc.Read(); } catch (ReaderException) { /* no sentinel */ }
        Assert.NotNull(factory.LastExpectedName);
        Assert.Equal("test.exe", factory.LastExpectedName);
    }

    [Fact]
    public void Factory_Open_PidOnly_PassesNullName()
    {
        var factory = new NameCapturingFactory();
        factory.Register(42);
        var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: factory);
        try { svc.Read(); } catch (ReaderException) { }
        Assert.Null(factory.LastExpectedName);
    }

    // ────────────────────────────────────────────────────────────────
    // 18. Native layout validation (Windows-only)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SystemInfo_Size_MatchesExpected()
    {
        int size = Marshal.SizeOf<NativeMethods.SYSTEM_INFO>();
        // On x64: 48 bytes
        Assert.True(size >= 48, $"SYSTEM_INFO size {size} expected >= 48");
    }

    [Fact]
    public void MemoryBasicInformation64_Size_MatchesExpected()
    {
        int size = Marshal.SizeOf<NativeMethods.MEMORY_BASIC_INFORMATION64>();
        Assert.Equal(48, size);
    }

    [Fact]
    public void MemoryBasicInformation64_KeyFieldOffsets_AreCorrect()
    {
        // BaseAddress at offset 0, RegionSize at offset 24
        Assert.Equal(0, Marshal.OffsetOf<NativeMethods.MEMORY_BASIC_INFORMATION64>("BaseAddress").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeMethods.MEMORY_BASIC_INFORMATION64>("RegionSize").ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<NativeMethods.MEMORY_BASIC_INFORMATION64>("State").ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<NativeMethods.MEMORY_BASIC_INFORMATION64>("Protect").ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<NativeMethods.MEMORY_BASIC_INFORMATION64>("Type").ToInt32());
    }

    [Fact]
    public void SelfProcess_AttachAndEnumerate_Succeeds()
    {
        // Guard: only on Windows x64
        if (!OperatingSystem.IsWindows()) return;
        if (nint.Size != 8) return;

        int pid = Environment.ProcessId;
        using var reader = WindowsMemoryReader.Attach(pid, expectedName: null);

        Assert.Equal(pid, reader.ProcessId);
        Assert.True(reader.IsAlive);
        Assert.True(reader.CheckLiveness());

        var regions = reader.QueryReadableRegions();
        Assert.NotEmpty(regions.Regions);
        Assert.True(regions.IsComplete);

        // Verify at least one region is above 4 GiB on x64
        Assert.Contains(regions.Regions, r => (long)r.BaseAddress > 0x1_0000_0000);

        reader.Dispose();
        // Double-dispose should be safe
        reader.Dispose();
    }

    private sealed class NameCapturingFactory : IMemoryReaderFactory
    {
        private readonly Dictionary<int, FakeMemoryCatalog> _procs = new();
        public string? LastExpectedName { get; private set; }
        public void Register(int pid) => _procs[pid] = new FakeMemoryCatalog();

        public IMemoryReader Open(int processId, string? expectedName = null)
        {
            LastExpectedName = expectedName;
            if (!_procs.TryGetValue(processId, out var cat))
                throw new ReaderException(ReaderFailureCode.ProcessNotFound, "Not found");
            return new FakeMemoryReader(cat, processId);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // 22. Process-loss race
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ProcessExit_DuringScan_ReturnsDisconnected()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        cat.MarkEnumerationIncomplete();
        var r = new FakeMemoryReader(cat, 1);
        var factory = new SingleReaderFactory(r);

        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 1 },
            TimeSpan.FromSeconds(5), readerFactory: factory);

        // First read: scan is incomplete, but process is still alive → Faulted+QueryFailure
        var r1 = svc.Read();
        Assert.False(r1.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, r1.ReadResult.TransportHealth);
        Assert.True(r1.FailureCode is ReaderFailureCode.QueryFailure);

        // Now kill the reader to simulate process exit
        r.Kill();

        // Second read: enumeration incomplete + process dead → Disconnected+ProcessExit
        var r2 = svc.Read();
        Assert.False(r2.IsUsable);
        Assert.Equal(ProviderHealth.Disconnected, r2.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.ProcessExit, r2.FailureCode);
        Assert.Equal(0, svc.AttachmentPid);
    }

    [Fact]
    public void CandidateLimitHit_IncrementsMetric()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 10; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x10000 + (i * 0x100000),
                sessionId: Guid.NewGuid(), seqA: (uint)(i * 2 + 1), seqB: (uint)(i * 2 + 2));
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: factory,
            scannerOptions: new SentinelScannerOptions { MaxCandidates = 2 });

        var result = svc.Read();
        Assert.False(result.IsUsable);
        Assert.True(result.Metrics.CandidateLimitHits > 0, "CandidateLimitHits should increment on cap failure");
        Assert.True(result.Metrics.ReadCycleFailures > 0, "ReadCycleFailures should increment on cap failure");
    }

    [Fact]
    public void StaleDiagnostic_CountsAsFailedCycle()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(10), readerFactory: factory, timeProvider: tp);

        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(0, r1.Metrics.ReadCycleFailures);

        tp.Advance(TimeSpan.FromMilliseconds(50));
        var r2 = svc.Read();
        Assert.Equal(ProviderHealth.Stale, r2.ReadResult.TransportHealth);
        Assert.True(r2.Metrics.ReadCycleFailures > r1.Metrics.ReadCycleFailures,
            "Stale diagnostics should count as failed read cycles");
        Assert.True(r2.Metrics.CacheMissCount > r1.Metrics.CacheMissCount,
            "Stale cache should increment cache-miss");
    }

    [Fact]
    public async Task ConcurrentDoubleDispose_IsIdempotent()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, cat);
        var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: factory);

        // Warm up
        Assert.True(svc.Read().IsUsable);

        // Concurrent dispose
        await Task.WhenAll(
            Task.Run(() => svc.Dispose()),
            Task.Run(() => svc.Dispose()));

        // After double-dispose, further Read throws
        Assert.Throws<ObjectDisposedException>(() => svc.Read());
    }
}
