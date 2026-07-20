using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;

namespace BotDs.Tests;

// ────────────────────────────────────────────────────────────────────
// Fake memory for deterministic scanner tests
// ────────────────────────────────────────────────────────────────────

internal sealed class FakeMemoryCatalog
{
    private readonly Dictionary<nint, byte[]> _pages = new();
    private readonly List<MemoryRegion> _regions = new();
    private bool _incompleteEnumeration;

    public void AddPage(nint baseAddress, byte[] data, bool isReadable = true)
    {
        _pages[baseAddress] = (byte[])data.Clone();
        if (isReadable)
            _regions.Add(new MemoryRegion(baseAddress, data.Length));
    }

    public void MarkEnumerationIncomplete() => _incompleteEnumeration = true;

    public RegionEnumerationResult GetRegions(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_regions.AsReadOnly(), !_incompleteEnumeration);
    }

    /// <summary>Replace data at an existing page (returns a mutable reference for modification).</summary>
    public void ModifyPage(nint baseAddress, int offset, byte[] newData)
    {
        if (_pages.TryGetValue(baseAddress, out var page))
            Array.Copy(newData, 0, page, offset, newData.Length);
    }

    public bool TryRead(nint address, byte[] buffer, int size)
    {
        long currentAddress = (long)address;
        int destinationOffset = 0;
        while (destinationOffset < size)
        {
            bool found = false;
            foreach (var (baseAddr, data) in _pages)
            {
                long offset = currentAddress - (long)baseAddr;
                if (offset < 0 || offset >= data.Length) continue;

                int copyLength = Math.Min(size - destinationOffset, data.Length - (int)offset);
                Array.Copy(data, (int)offset, buffer, destinationOffset, copyLength);
                currentAddress += copyLength;
                destinationOffset += copyLength;
                found = true;
                break;
            }

            if (!found) return false;
        }

        return true;
    }
}

internal sealed class FakeMemoryReader : IMemoryReader
{
    private readonly FakeMemoryCatalog _catalog;
    private volatile bool _alive = true;

    public FakeMemoryReader(FakeMemoryCatalog catalog, int processId)
    {
        _catalog = catalog;
        ProcessId = processId;
    }

    public int ProcessId { get; }
    public bool IsAlive => _alive;
    public bool CheckLiveness() => _alive;
    public void Kill() => _alive = false;

    public RegionEnumerationResult QueryReadableRegions(CancellationToken cancellationToken = default) =>
        _catalog.GetRegions(cancellationToken);

    public void ReadExact(nint address, byte[] buffer, int size)
    {
        if (!_alive)
            throw new ReaderException(ReaderFailureCode.ProcessExit, "Process exited");
        if (!_catalog.TryRead(address, buffer, size))
            throw new ReaderException(ReaderFailureCode.ReadFailure, "Read failed at (addr)");
    }

    public void Dispose() => _alive = false;
}

internal sealed class FakeMemoryReaderFactory : IMemoryReaderFactory
{
    private readonly Dictionary<int, FakeMemoryCatalog> _processes = new();
    private readonly Dictionary<int, FakeMemoryReader> _liveReaders = new();
    private readonly HashSet<int> _exitedProcesses = new();

    public void RegisterProcess(int pid, FakeMemoryCatalog catalog)
        => _processes[pid] = catalog;

    public void KillProcess(int pid) => _exitedProcesses.Add(pid);

    public FakeMemoryReader? GetLastReader(int pid)
        => _liveReaders.TryGetValue(pid, out var r) ? r : null;

    public IMemoryReader Open(int processId, string? expectedName = null)
    {
        if (_exitedProcesses.Contains(processId))
            throw new ReaderException(ReaderFailureCode.ProcessExit, "Process has exited");
        if (!_processes.TryGetValue(processId, out var catalog))
            throw new ReaderException(ReaderFailureCode.ProcessNotFound, "Not found");
        var reader = new FakeMemoryReader(catalog, processId);
        _liveReaders[processId] = reader;
        return reader;
    }
}

internal sealed class ThrowingMemoryReaderFactory(ReaderException exception) : IMemoryReaderFactory
{
    public IMemoryReader Open(int processId, string? expectedName = null) => throw exception;
}

// ────────────────────────────────────────────────────────────────────
// Helpers
// ────────────────────────────────────────────────────────────────────

internal static class ScannerTestHelpers
{
    public static byte[] BuildSentinelBytes(uint totalSize = V5Constants.RegionTotalSize,
        uint slotSize = V5Constants.BufferSlotSize)
    {
        byte[] sentinel = new byte[V5Constants.SentinelSize];
        "BotDsV05"u8.CopyTo(sentinel);
        BitConverter.TryWriteBytes(sentinel.AsSpan(8), totalSize);
        BitConverter.TryWriteBytes(sentinel.AsSpan(12), slotSize);
        return sentinel;
    }

    public static byte[] BuildSlot(uint sequence, Guid sessionId,
        uint producerFrameMs = 0, uint maxAgeMs = 500)
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), producerFrameMs);

        byte[] provider = new byte[28];
        sessionId.TryWriteBytes(provider);
        BitConverter.TryWriteBytes(provider.AsSpan(16), producerFrameMs);
        BitConverter.TryWriteBytes(provider.AsSpan(20), maxAgeMs);
        provider[26] = 1;

        int payloadOffset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset), (ushort)V5Constants.SectionTypeProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset + 2), (ushort)provider.Length);
        provider.CopyTo(slot, payloadOffset + V5Constants.SectionHeaderSize);
        uint payloadLength = (uint)(provider.Length + V5Constants.SectionHeaderSize);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), payloadLength);
        V5Crc32.WriteCrc(slot, payloadLength);
        return slot;
    }

    /// <summary>Place a complete V5 region in a catalog at base address.</summary>
    public static void PlaceV5Region(FakeMemoryCatalog catalog, nint baseAddr,
        byte[]? sentinelOverride = null, byte[]? slotAOverride = null,
        byte[]? slotBOverride = null, Guid? sessionId = null,
        uint seqA = 1, uint seqB = 2, uint producerFrameMs = 0)
    {
        Guid sid = sessionId ?? Guid.NewGuid();
        byte[] sentinel = sentinelOverride ?? BuildSentinelBytes();
        byte[] bufferA = slotAOverride ?? BuildSlot(seqA, sid, producerFrameMs);
        byte[] bufferB = slotBOverride ?? BuildSlot(seqB, sid, producerFrameMs);

        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        bufferA.CopyTo(region, V5Constants.BufferAOffset);
        bufferB.CopyTo(region, V5Constants.BufferBOffset);
        catalog.AddPage(baseAddr, region);
    }

    /// <summary>Build a slot with a specific ProviderInfo ProducerFrameMs that differs from header.</summary>
    public static byte[] BuildSlotWithMismatchedProducerFrame(uint sequence, Guid sessionId,
        uint headerProducerMs, uint providerProducerMs)
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), headerProducerMs);

        byte[] provider = new byte[28];
        sessionId.TryWriteBytes(provider);
        BitConverter.TryWriteBytes(provider.AsSpan(16), providerProducerMs);
        BitConverter.TryWriteBytes(provider.AsSpan(20), 500u);
        provider[26] = 1;

        int payloadOffset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset), (ushort)V5Constants.SectionTypeProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset + 2), (ushort)provider.Length);
        provider.CopyTo(slot, payloadOffset + V5Constants.SectionHeaderSize);
        uint payloadLength = (uint)(provider.Length + V5Constants.SectionHeaderSize);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), payloadLength);
        V5Crc32.WriteCrc(slot, payloadLength);
        return slot;
    }

    public static byte[] BuildBadSizeSentinel()
    {
        byte[] sentinel = new byte[V5Constants.SentinelSize];
        "BotDsV05"u8.CopyTo(sentinel);
        BitConverter.TryWriteBytes(sentinel.AsSpan(8), 99999u);
        BitConverter.TryWriteBytes(sentinel.AsSpan(12), 99999u);
        return sentinel;
    }

    /// <summary>Fill a slot directly (mirrors ProtocolTests FillSlot for use in scanner tests).</summary>
    public static void FillSlot(byte[] slot, uint sequence, Guid? sessionId = null, uint producerFrameMs = 0)
    {
        Array.Clear(slot);
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrProducerFrameMsOffset), producerFrameMs);

        byte[] provider = new byte[28];
        (sessionId ?? Guid.NewGuid()).TryWriteBytes(provider);
        BitConverter.TryWriteBytes(provider.AsSpan(16), producerFrameMs);
        BitConverter.TryWriteBytes(provider.AsSpan(20), 500u);
        provider[26] = 1;

        int payloadOffset = V5Constants.PayloadOffset;
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset), (ushort)V5Constants.SectionTypeProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(payloadOffset + 2), (ushort)provider.Length);
        provider.CopyTo(slot, payloadOffset + V5Constants.SectionHeaderSize);
        uint payloadLength = (uint)(provider.Length + V5Constants.SectionHeaderSize);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSectionsMaskOffset), V5Constants.MaskProviderInfo);
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrPayloadLengthOffset), payloadLength);
        V5Crc32.WriteCrc(slot, payloadLength);
    }

    /// <summary>Build a slot without ProviderInfo section.</summary>
    public static byte[] BuildSlotWithoutProvider(uint sequence)
    {
        byte[] slot = new byte[V5Constants.BufferSlotSize];
        slot[V5Constants.HdrProtocolVersionOffset] = V5Constants.ProtocolVersion;
        BitConverter.TryWriteBytes(slot.AsSpan(V5Constants.HdrSequenceOffset), sequence);
        V5Crc32.WriteCrc(slot, payloadLength: 0);
        return slot;
    }
}

// ────────────────────────────────────────────────────────────────────
// Process Selector Tests
// ────────────────────────────────────────────────────────────────────

public sealed class ProcessSelectorTests
{
    [Fact]
    public void NormalizeName_StripsExeAndLowercases()
    {
        Assert.Equal("rift", ProcessSelector.NormalizeName("RIFT.EXE"));
        Assert.Equal("rift", ProcessSelector.NormalizeName("  rift.exe  "));
        Assert.Equal("rift", ProcessSelector.NormalizeName("RIFT"));
    }

    [Fact]
    public void InvalidSelector_Empty_IsNotValid()
    {
        Assert.False(new ProcessSelector().IsValid);
    }

    [Fact]
    public void NegativePid_NotValid()
    {
        Assert.False(new ProcessSelector { ProcessId = -1 }.IsValid);
        Assert.False(new ProcessSelector { ProcessId = 0 }.IsValid);
        // Negative PID with valid name must still be invalid — never fall back to name discovery.
        Assert.False(new ProcessSelector { ProcessId = -1, ProcessName = "rift.exe" }.IsValid);
        Assert.False(new ProcessSelector { ProcessId = 0, ProcessName = "rift.exe" }.IsValid);
    }

    [Fact]
    public void NameOnly_IsValid()
    {
        Assert.True(new ProcessSelector { ProcessName = "rift.exe" }.IsValid);
    }

    [Fact]
    public void NameWithPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProcessSelector { ProcessName = "C:\\rift.exe" });
        Assert.Throws<ArgumentException>(() => new ProcessSelector { ProcessName = "dir/rift" });
    }

    [Fact]
    public void NameWithWildcard_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProcessSelector { ProcessName = "rift*" });
        Assert.Throws<ArgumentException>(() => new ProcessSelector { ProcessName = "ri?t" });
    }

    [Fact]
    public void EmptyNameAfterNormalization_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ProcessSelector { ProcessName = ".exe" });
    }

    [Fact]
    public void NameMatches_ExactMatch()
    {
        var s = new ProcessSelector { ProcessName = "rift.exe" };
        Assert.True(s.NameMatches("RIFT.EXE"));
        Assert.True(s.NameMatches("rift"));
        Assert.False(s.NameMatches("notepad.exe"));
    }
}

// ────────────────────────────────────────────────────────────────────
// Scanner Metrics
// ────────────────────────────────────────────────────────────────────

public sealed class ScannerMetricsTests
{
    [Fact]
    public void Metrics_HasNoAddressesNamesOrPaths()
    {
        var m = new ScannerMetrics { FullScanCount = 5, BytesScanned = 1_000_000 };
        string json = System.Text.Json.JsonSerializer.Serialize(m);
        Assert.DoesNotContain("0x", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".exe", json, StringComparison.OrdinalIgnoreCase);
    }
}

// ────────────────────────────────────────────────────────────────────
// Native memory filtering
// ────────────────────────────────────────────────────────────────────

public sealed class NativeMemoryFilteringTests
{
    [Theory]
    [InlineData(NativeMethods.PAGE_READONLY)]
    [InlineData(NativeMethods.PAGE_READWRITE)]
    [InlineData(NativeMethods.PAGE_WRITECOPY)]
    [InlineData(NativeMethods.PAGE_EXECUTE_READ)]
    [InlineData(NativeMethods.PAGE_EXECUTE_READWRITE)]
    [InlineData(NativeMethods.PAGE_EXECUTE_WRITECOPY)]
    public void IsProtectionReadable_AllowsOnlyDocumentedReadableBaseProtections(uint protection)
    {
        Assert.True(NativeMethods.IsProtectionReadable(protection));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(NativeMethods.PAGE_NOACCESS)]
    [InlineData(NativeMethods.PAGE_EXECUTE)]
    [InlineData(0x0Fu)]
    [InlineData(NativeMethods.PAGE_READWRITE | NativeMethods.PAGE_GUARD)]
    public void IsProtectionReadable_RejectsUnsafeOrUnknownProtections(uint protection)
    {
        Assert.False(NativeMethods.IsProtectionReadable(protection));
    }

    [Fact]
    public void RegionBounds_UsesCheckedPositiveLongArithmeticBeforeClamping()
    {
        Assert.True(WindowsMemoryReader.TryCalculateRegionBounds(
            rawBase: 100,
            rawSize: 100,
            currAddr: 100,
            maxAddr: 150,
            out long baseAddress,
            out long nextAddress,
            out long clampedEnd));
        Assert.Equal(100, baseAddress);
        Assert.Equal(200, nextAddress);
        Assert.Equal(150, clampedEnd);

        Assert.False(WindowsMemoryReader.TryCalculateRegionBounds(100, 0, 100, 1_000, out _, out _, out _));
        Assert.False(WindowsMemoryReader.TryCalculateRegionBounds(100, (ulong)long.MaxValue + 1, 100, 1_000, out _, out _, out _));
        Assert.False(WindowsMemoryReader.TryCalculateRegionBounds((ulong)long.MaxValue - 5, 10, long.MaxValue - 5, long.MaxValue, out _, out _, out _));
        Assert.False(WindowsMemoryReader.TryCalculateRegionBounds(99, 10, 100, 1_000, out _, out _, out _));
    }
}

// ────────────────────────────────────────────────────────────────────
// Sentinel Scanner
// ────────────────────────────────────────────────────────────────────

public sealed class V5SentinelScannerTests
{
    [Fact]
    public void Scan_SingleCandidate_FindsIt()
    {
        var cat = new FakeMemoryCatalog();
        nint addr = 0x7FFE0000;
        ScannerTestHelpers.PlaceV5Region(cat, addr, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var r = new FakeMemoryReader(cat, 1);

        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Single(result.Candidates);
        Assert.Equal(addr, result.Candidates[0].BaseAddress);
        Assert.Equal(1, result.RawMagicMatches);
        Assert.Equal(1, result.ExactSentinelMatches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void Scan_MagicOverlapAcrossChunks_FindsSplitSentinel()
    {
        // Place sentinel so that magic "BotDsV05" crosses a chunk boundary.
        // ChunkSize=256, sentinel starts at offset 250 of chunk → magic bytes
        // spread across chunk boundary.
        var cat = new FakeMemoryCatalog();
        nint regionBase = 0x1000000;
        int paddingBefore = 250;
        byte[] sentinel = ScannerTestHelpers.BuildSentinelBytes();
        byte[] slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        byte[] slotB = ScannerTestHelpers.BuildSlot(2, Guid.NewGuid());

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

    [Fact]
    public void Scan_MagicAcrossContiguousRegions_FindsCandidate()
    {
        var cat = new FakeMemoryCatalog();
        nint baseAddress = 0x100000;
        const int firstRegionSize = 300;
        const int bytesBeforeSplit = 4;
        byte[] layout = new byte[V5Constants.RegionTotalSize];
        Guid session = Guid.NewGuid();
        ScannerTestHelpers.BuildSentinelBytes().CopyTo(layout, 0);
        ScannerTestHelpers.BuildSlot(1, session).CopyTo(layout, V5Constants.BufferAOffset);
        ScannerTestHelpers.BuildSlot(2, session).CopyTo(layout, V5Constants.BufferBOffset);

        byte[] firstRegion = new byte[firstRegionSize];
        layout.AsSpan(0, bytesBeforeSplit).CopyTo(firstRegion.AsSpan(firstRegionSize - bytesBeforeSplit));
        byte[] secondRegion = layout[bytesBeforeSplit..];
        cat.AddPage(baseAddress, firstRegion);
        cat.AddPage(baseAddress + firstRegionSize, secondRegion);

        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        V5Candidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(baseAddress + firstRegionSize - bytesBeforeSplit, candidate.BaseAddress);
        Assert.Equal(1, result.RawMagicMatches);
        Assert.Equal(1, result.ExactSentinelMatches);
        Assert.False(result.Incomplete);
    }

    [Fact]
    public void Scan_MaxRawMatches_Incomplete()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 50; i++)
        {
            var sentinel = ScannerTestHelpers.BuildBadSizeSentinel();
            var page = new byte[V5Constants.SentinelSize + 20];
            sentinel.CopyTo(page, i % 10);
            cat.AddPage(0x10000 + (i * 0x1000), page);
        }
        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { MaxRawMatches = 10 };
        var result = V5SentinelScanner.Scan(r, opts);
        Assert.True(result.Incomplete);
        Assert.Equal(ScanIncompleteCause.RawMatchLimitExceeded, result.IncompleteCause);
    }

    [Fact]
    public void Scan_MaxCandidates_Incomplete()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 10; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x10000 + (i * 0x100000),
                sessionId: Guid.NewGuid(), seqA: (uint)(i * 2 + 1), seqB: (uint)(i * 2 + 2));
        var r = new FakeMemoryReader(cat, 1);
        var opts = new SentinelScannerOptions { MaxCandidates = 3 };
        var result = V5SentinelScanner.Scan(r, opts);
        Assert.True(result.Incomplete);
        Assert.Equal(ScanIncompleteCause.CandidateLimitExceeded, result.IncompleteCause);
        Assert.Equal(3, result.Candidates.Count);
    }

    [Fact]
    public void Scan_ProducerFrameMsMismatch_Rejected()
    {
        var cat = new FakeMemoryCatalog();
        var sid = Guid.NewGuid();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(1, sid, 100, 200);
        var slotB = ScannerTestHelpers.BuildSlot(2, sid, 0);
        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x10000, region);
        var r = new FakeMemoryReader(cat, 1);

        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        // Buffer A is rejected due to mismatch, but Buffer B is still valid
        Assert.Single(result.Candidates);
        Assert.Null(result.Candidates[0].FrameA); // A rejected
        Assert.NotNull(result.Candidates[0].FrameB); // B valid
    }

    [Fact]
    public void Scan_NoProviderInfo_Rejected()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlotWithoutProvider(1);
        var slotB = ScannerTestHelpers.BuildSlotWithoutProvider(2);
        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        slotB.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x10000, region);
        var r = new FakeMemoryReader(cat, 1);

        // Both slots rejected because they lack ProviderInfo → no candidate
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Scan_OneValidSlot_Accepted()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        var invalidSlot = new byte[V5Constants.BufferSlotSize];
        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        invalidSlot.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x10000, region);
        var r = new FakeMemoryReader(cat, 1);
        var result = V5SentinelScanner.Scan(r, new SentinelScannerOptions());
        Assert.Single(result.Candidates);
        Assert.NotNull(result.Candidates[0].FrameA);
        Assert.Null(result.Candidates[0].FrameB);
    }

    [Fact]
    public void ValidateCandidate_Valid_ReturnsCandidate()
    {
        var cat = new FakeMemoryCatalog();
        nint addr = 0x80000;
        ScannerTestHelpers.PlaceV5Region(cat, addr, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var r = new FakeMemoryReader(cat, 1);
        var c = V5SentinelScanner.ValidateCandidate(r, addr);
        Assert.NotNull(c);
        Assert.True(c.IsValid);
    }
}

// ────────────────────────────────────────────────────────────────────
// V5FrameSelector
// ────────────────────────────────────────────────────────────────────

public sealed class V5FrameSelectorTests
{
    private static readonly Guid SA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Select_SameSession_NewerSequence_Wins()
    {
        var r = V5FrameSelector.Select(MkFrame(SA, 3), MkFrame(SA, 5), out var s);
        Assert.Equal(V5SelectionResult.Selected, r);
        Assert.Equal(5u, s!.Header.Sequence);
    }

    [Fact]
    public void Select_SameSession_Wrap_SelectsNewer()
    {
        var r = V5FrameSelector.Select(MkFrame(SA, uint.MaxValue - 1), MkFrame(SA, 1), out var s);
        Assert.Equal(V5SelectionResult.Selected, r);
        Assert.Equal(1u, s!.Header.Sequence);
    }

    [Fact]
    public void Select_DifferentSessions_NewerProducerTime_Wins()
    {
        var r = V5FrameSelector.Select(MkFrame(SA, 100, 1000), MkFrame(SB, 1, 1010), out var s);
        Assert.Equal(V5SelectionResult.Selected, r);
        Assert.Equal(SB, s!.Provider!.SessionId);
    }

    [Fact]
    public void Select_AmbiguousHalfRange_Fails()
    {
        var r = V5FrameSelector.Select(MkFrame(SA, 0), MkFrame(SA, 0x80000000), out _);
        Assert.Equal(V5SelectionResult.Ambiguous, r);
    }

    [Fact]
    public void Select_NoProvider_FailsAmbiguous()
    {
        var fa = MkFrameNoProvider(SA, 1);
        var fb = MkFrameNoProvider(SA, 2);
        var r = V5FrameSelector.Select(fa, fb, out _);
        Assert.Equal(V5SelectionResult.Ambiguous, r);
    }

    [Fact]
    public void SelectBest_OrderIndependent()
    {
        var c1 = MkCand(0x1000, SA, 1, 2);
        var c2 = MkCand(0x2000, SA, 5, 6);
        var c3 = MkCand(0x3000, SA, 3, 4);
        V5Candidate[][] permutations =
        [
            [c1, c2, c3], [c1, c3, c2], [c2, c1, c3],
            [c2, c3, c1], [c3, c1, c2], [c3, c2, c1],
        ];

        foreach (V5Candidate[] permutation in permutations)
        {
            V5SelectionResult result = V5FrameSelector.SelectBest(permutation, out V5Candidate? selected);
            Assert.Equal(V5SelectionResult.Selected, result);
            Assert.Equal(0x2000, selected!.BaseAddress);
        }
    }

    [Fact]
    public void SelectBest_CyclicSerialOrdering_IsAmbiguousForEveryPermutation()
    {
        var c1 = MkCandOneSlot(0x1000, SA, 0x00000000);
        var c2 = MkCandOneSlot(0x2000, SA, 0x60000000);
        var c3 = MkCandOneSlot(0x3000, SA, 0xC0000000);
        V5Candidate[][] permutations =
        [
            [c1, c2, c3], [c1, c3, c2], [c2, c1, c3],
            [c2, c3, c1], [c3, c1, c2], [c3, c2, c1],
        ];

        foreach (V5Candidate[] permutation in permutations)
        {
            Assert.Equal(
                V5SelectionResult.Ambiguous,
                V5FrameSelector.SelectBest(permutation, out V5Candidate? selected));
            Assert.Null(selected);
        }
    }

    [Fact]
    public void SelectBest_CrossSessionProducerTime_IsOrderIndependent()
    {
        var c1 = MkCandOneSlot(0x1000, SA, 100, producerMs: 1_000);
        var c2 = MkCandOneSlot(0x2000, SB, 1, producerMs: 1_200);
        var c3 = MkCandOneSlot(0x3000, Guid.Parse("33333333-3333-3333-3333-333333333333"), 50, producerMs: 1_100);
        V5Candidate[][] permutations =
        [
            [c1, c2, c3], [c1, c3, c2], [c2, c1, c3],
            [c2, c3, c1], [c3, c1, c2], [c3, c2, c1],
        ];

        foreach (V5Candidate[] permutation in permutations)
        {
            Assert.Equal(
                V5SelectionResult.Selected,
                V5FrameSelector.SelectBest(permutation, out V5Candidate? selected));
            Assert.Equal(0x2000, selected!.BaseAddress);
        }
    }

    [Fact]
    public void StableReader_UsesSameProducerConsistencyValidityAsProbe()
    {
        Guid session = Guid.NewGuid();
        byte[] inconsistent = ScannerTestHelpers.BuildSlotWithMismatchedProducerFrame(100, session, 10, 20);
        byte[] valid = ScannerTestHelpers.BuildSlot(2, session, producerFrameMs: 10);
        var reader = new StableReader(TimeSpan.FromSeconds(1));

        StableReadResult result = reader.Read(
            destination => inconsistent.CopyTo(destination),
            destination => valid.CopyTo(destination),
            DateTimeOffset.UtcNow);

        Assert.True(result.IsUsable, result.FailureDetail);
        Assert.Equal(2u, result.Frame!.Header.Sequence);
    }

    [Fact]
    public void SelectBest_MixedSlotCounts_ComparesByBestFrame()
    {
        // c1 has one slot (seq=10), c2 has two slots (seq=5 and seq=7), c3 has one slot (seq=3)
        var c1 = MkCandOneSlot(0x1000, SA, 10);
        var c2 = MkCand(0x2000, SA, 5, 7);
        var c3 = MkCandOneSlot(0x3000, SA, 3);
        var r = V5FrameSelector.SelectBest([c1, c2, c3], out var s);
        Assert.Equal(V5SelectionResult.Selected, r);
        Assert.Equal(0x1000, s!.BaseAddress); // seq 10 newest
    }

    private static ParsedV5Frame MkFrame(Guid sid, uint seq, uint producerMs = 0)
    {
        return new ParsedV5Frame(
            new V5BufferHeader { Sequence = seq, ProducerFrameMs = producerMs, ProtocolVersion = 5 },
            new ParsedProviderInfo(sid, producerMs, 500, "t", 1),
            null, null, [], [], [], 0);
    }

    private static ParsedV5Frame MkFrameNoProvider(Guid sid, uint seq)
    {
        return new ParsedV5Frame(
            new V5BufferHeader { Sequence = seq, ProtocolVersion = 5 },
            null, null, null, [], [], [], 0);
    }

    private static V5Candidate MkCand(nint addr, Guid sid, uint seqA, uint seqB)
    {
        var s = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize };
        return new V5Candidate { BaseAddress = addr, Sentinel = s, FrameA = MkFrame(sid, seqA), FrameB = MkFrame(sid, seqB) };
    }

    private static V5Candidate MkCandOneSlot(nint addr, Guid sid, uint seq, uint producerMs = 0)
    {
        var s = new V5Sentinel { TotalSize = V5Constants.RegionTotalSize, BufferSlotSize = V5Constants.BufferSlotSize };
        return new V5Candidate { BaseAddress = addr, Sentinel = s, FrameA = MkFrame(sid, seq, producerMs), FrameB = null };
    }
}

// ────────────────────────────────────────────────────────────────────
// V5ScannerService
// ────────────────────────────────────────────────────────────────────

public sealed class V5ScannerServiceTests
{
    [Fact]
    public void Constructor_InvalidSelector_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new V5ScannerService(new ProcessSelector(), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Read_ProcessNotFound_ReturnsFailure()
    {
        using var svc = new V5ScannerService(
            new ProcessSelector { ProcessId = 9999 }, TimeSpan.FromSeconds(5),
            readerFactory: new FakeMemoryReaderFactory());
        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.ProcessNotFound, r.FailureCode);
    }

    [Fact]
    public void Read_ValidRegion_ReturnsUsable()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r = svc.Read();
        Assert.True(r.IsUsable, r.ReadResult.FailureDetail ?? r.FailureCode.ToString());
        Assert.Equal(42, r.AttachmentPid);
        Assert.Equal(1, r.AttachmentGeneration);
        Assert.Equal(1, r.Metrics.RawMagicMatchesFound);
        Assert.Equal(1, r.Metrics.ExactSentinelMatchesFound);
        Assert.Equal(1, r.Metrics.ValidCandidatesFound);
    }

    [Fact]
    public void Read_CacheHit_SecondReadUsesCache()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(1, r1.Metrics.FullScanCount);
        var r2 = svc.Read();
        Assert.True(r2.IsUsable);
        Assert.Equal(1, r2.Metrics.FullScanCount);
        Assert.Equal(1, r2.Metrics.CacheHitCount);
        Assert.Equal(1, r2.AttachmentGeneration);
    }

    [Fact]
    public void Read_ReattachCreatesNewGenerationButOrdinaryReadsDoNot()
    {
        var catalog = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(catalog, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, catalog);
        using var service = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5), readerFactory: factory);

        Assert.Equal(1, service.Read().AttachmentGeneration);
        Assert.Equal(1, service.Read().AttachmentGeneration);
        factory.GetLastReader(42)!.Kill();

        ScannerReadResult reattached = service.Read();
        Assert.True(reattached.IsUsable, reattached.ReadResult.FailureDetail);
        Assert.Equal(2, reattached.AttachmentGeneration);
        Assert.Equal(2, reattached.Metrics.AttachmentCount);
    }

    [Fact]
    public void Read_StaleTriggersRelocation_Test()
    {
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: sid, seqA: 1, seqB: 2);

        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(100), readerFactory: fact, timeProvider: tp);

        // First read caches the sentinel
        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(1, r1.Metrics.FullScanCount);

        // Advance past max age
        tp.Advance(TimeSpan.FromMilliseconds(500));

        // Second read: stale triggers relocation (at most one scan in same cycle)
        var r2 = svc.Read();
        Assert.Equal(2, r2.Metrics.FullScanCount); // relocation scan performed
        Assert.False(r2.IsUsable);
        Assert.Equal(ProviderHealth.Stale, r2.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.StaleTelemetry, r2.FailureCode);
    }

    [Fact]
    public void Read_IncompleteScan_ReturnsFaulted()
    {
        var cat = new FakeMemoryCatalog();
        // Fill with many decoys to hit max raw match limit
        for (int i = 0; i < 100; i++)
        {
            var sentinel = ScannerTestHelpers.BuildBadSizeSentinel();
            var page = new byte[V5Constants.SentinelSize + 20];
            sentinel.CopyTo(page, i % 10);
            cat.AddPage(0x10000 + (i * 0x1000), page);
        }
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var opts = new SentinelScannerOptions { MaxRawMatches = 10 };
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact, scannerOptions: opts);
        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.CandidateLimitExceeded, r.FailureCode);
        Assert.Equal(ProviderHealth.Faulted, r.ReadResult.TransportHealth);
    }

    [Fact]
    public void Read_ProcessExitThroughFactory_ReturnsDisconnected()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);

        Assert.True(svc.Read().IsUsable);

        // Kill the reader AND mark process as exited so re-attach fails
        fact.GetLastReader(42)?.Kill();
        fact.KillProcess(42);

        var r2 = svc.Read();
        Assert.False(r2.IsUsable);
        Assert.Equal(ReaderFailureCode.ProcessExit, r2.FailureCode);
        Assert.Equal(ProviderHealth.Disconnected, r2.ReadResult.TransportHealth);
        Assert.Equal(0, svc.AttachmentPid);
    }

    [Fact]
    public void Read_NoSentinel_ReturnsDisconnected()
    {
        var cat = new FakeMemoryCatalog();
        cat.AddPage(0x1000000, new byte[0x100000]);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.SentinelNotFound, r.FailureCode);
        Assert.Equal(ProviderHealth.Disconnected, r.ReadResult.TransportHealth);
    }

    [Fact]
    public void Read_CyclicCandidateOrdering_ReturnsFaultedAmbiguous()
    {
        var catalog = new FakeMemoryCatalog();
        Guid session = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(catalog, 0x100000, sessionId: session, seqA: 0, seqB: 0);
        ScannerTestHelpers.PlaceV5Region(catalog, 0x200000, sessionId: session, seqA: 0x60000000, seqB: 0x60000000);
        ScannerTestHelpers.PlaceV5Region(catalog, 0x300000, sessionId: session, seqA: 0xC0000000, seqB: 0xC0000000);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, catalog);
        using var service = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5), readerFactory: factory);

        ScannerReadResult result = service.Read();

        Assert.False(result.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, result.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.CandidateAmbiguous, result.FailureCode);
        Assert.Equal(3, result.Metrics.ValidCandidatesFound);
    }

    [Fact]
    public void Read_EmptySlots_ReturnsFaultedCandidateInvalid()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var invalidSlot = new byte[V5Constants.BufferSlotSize];
        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        invalidSlot.CopyTo(region, V5Constants.BufferAOffset);
        invalidSlot.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ProviderHealth.Faulted, r.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.CandidateInvalid, r.FailureCode);
        Assert.Equal(1, r.Metrics.RawMagicMatchesFound);
        Assert.Equal(1, r.Metrics.ExactSentinelMatchesFound);
        Assert.Equal(0, r.Metrics.ValidCandidatesFound);
    }

    [Fact]
    public void Read_Disposal_PreventsFurtherReads()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        Assert.True(svc.Read().IsUsable);
        svc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => svc.Read());
    }

    [Fact]
    public void Read_Cancellation_Rethrows()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => svc.Read(cts.Token));
    }

    [Fact]
    public void Read_MetricsPrivacy_CorrectShape()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r = svc.Read();
        Assert.True(r.IsUsable);
        string json = System.Text.Json.JsonSerializer.Serialize(r.Metrics);
        Assert.DoesNotContain("0x", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rift", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_OneValidSlot_ReturnsFrame()
    {
        var cat = new FakeMemoryCatalog();
        var sentinel = ScannerTestHelpers.BuildSentinelBytes();
        var slotA = ScannerTestHelpers.BuildSlot(1, Guid.NewGuid());
        var invalidSlot = new byte[V5Constants.BufferSlotSize];
        byte[] region = new byte[V5Constants.RegionTotalSize];
        sentinel.CopyTo(region, V5Constants.SentinelOffset);
        slotA.CopyTo(region, V5Constants.BufferAOffset);
        invalidSlot.CopyTo(region, V5Constants.BufferBOffset);
        cat.AddPage(0x1000000, region);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);
        var r = svc.Read();
        Assert.True(r.IsUsable, $"{r.FailureCode}: {r.ReadResult.FailureDetail}");
    }

    [Fact]
    public void Read_SequenceGap_ReturnsNonUsableContinuityDiagnostic()
    {
        var cat = new FakeMemoryCatalog();
        nint address = 0x1000000;
        Guid session = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(cat, address, sessionId: session, seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, cat);
        using var service = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, TimeSpan.FromSeconds(5), readerFactory: factory);

        Assert.True(service.Read().IsUsable);
        cat.ModifyPage(address, V5Constants.BufferAOffset, ScannerTestHelpers.BuildSlot(4, session));
        cat.ModifyPage(address, V5Constants.BufferBOffset, ScannerTestHelpers.BuildSlot(5, session));

        ScannerReadResult result = service.Read();
        Assert.False(result.IsUsable);
        Assert.Equal(ProviderHealth.Degraded, result.ReadResult.TransportHealth);
        Assert.Equal(ContinuityResult.Gap, result.ReadResult.Continuity);
        Assert.Equal(ReaderFailureCode.ContinuityDegraded, result.FailureCode);
        Assert.NotNull(result.Frame);
    }

    [Fact]
    public void Read_StableReaderCallbackExceptions_Survive()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);

        Assert.True(svc.Read().IsUsable);

        // Kill the underlying reader and mark process as exited
        fact.GetLastReader(42)?.Kill();
        fact.KillProcess(42);

        // Next read: callback throws ReaderException, StableReader rethrows,
        // V5ScannerService catches and sanitizes
        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.ProcessExit, r.FailureCode);
    }

    [Fact]
    public void Read_FailureDetails_Sanitized()
    {
        var factory = new ThrowingMemoryReaderFactory(new ReaderException(
            ReaderFailureCode.OpenFailure,
            "Failed at 0x7FFE0000 while loading C:\\private\\reader.dll " + new string('X', 500)));
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: factory);
        var r = svc.Read();
        Assert.Equal(ReaderFailureCode.OpenFailure, r.FailureCode);
        Assert.NotNull(r.ReadResult.FailureDetail);
        Assert.DoesNotContain("7FFE0000", r.ReadResult.FailureDetail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", r.ReadResult.FailureDetail, StringComparison.Ordinal);
        Assert.True(r.ReadResult.FailureDetail!.Length <= 200);
    }

    // ── Stale backoff ───────────────────────────────────────────

    [Fact]
    public void StaleBackoff_SuppressesRepeatedRescans()
    {
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: sid, seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(10), readerFactory: fact, timeProvider: tp);

        // First read — healthy, caches sentinel
        var r1 = svc.Read();
        Assert.True(r1.IsUsable);
        Assert.Equal(1, r1.Metrics.FullScanCount);

        // Advance past max age → stale triggers relocation (full scan #2)
        tp.Advance(TimeSpan.FromMilliseconds(500));

        var r2 = svc.Read();
        Assert.False(r2.IsUsable);
        Assert.Equal(ProviderHealth.Stale, r2.ReadResult.TransportHealth);
        Assert.Equal(2, r2.Metrics.FullScanCount); // relocation scan performed
        Assert.Equal(0, r2.Metrics.CacheHitCount);
        Assert.Equal(2, r2.Metrics.CacheMissCount);

        // Third read within backoff window — must NOT trigger another relocation scan
        tp.Advance(TimeSpan.FromMilliseconds(10));
        var r3 = svc.Read();
        Assert.False(r3.IsUsable);
        Assert.Equal(ProviderHealth.Stale, r3.ReadResult.TransportHealth);
        Assert.Equal(2, r3.Metrics.FullScanCount); // no additional scan

        // Advance past backoff interval (5s default) — scan allowed again
        tp.Advance(TimeSpan.FromSeconds(10));
        var r4 = svc.Read();
        Assert.False(r4.IsUsable);
        Assert.Equal(ProviderHealth.Stale, r4.ReadResult.TransportHealth);
        // The cached stale candidate is revalidated, then one relocation scan is allowed.
        Assert.Equal(3, r4.Metrics.FullScanCount);
    }

    [Fact]
    public void StaleBackoff_StaleCandidateFoundByScanIsNotScannedTwiceInCycle()
    {
        var cat = new FakeMemoryCatalog();
        Guid sessionId = Guid.NewGuid();
        nint firstAddress = 0x1000000;
        ScannerTestHelpers.PlaceV5Region(cat, firstAddress, sessionId: sessionId, seqA: 1, seqB: 2);
        ScannerTestHelpers.PlaceV5Region(cat, 0x2000000, sessionId: sessionId, seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(10), readerFactory: fact, timeProvider: tp);

        Assert.True(svc.Read().IsUsable);
        tp.Advance(TimeSpan.FromSeconds(1));
        cat.ModifyPage(firstAddress, 0, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);

        ScannerReadResult result = svc.Read();

        Assert.Equal(ProviderHealth.Stale, result.ReadResult.TransportHealth);
        Assert.Equal(2, result.Metrics.FullScanCount);
        Assert.Equal(2, result.Metrics.CacheMissCount);
    }

    [Fact]
    public void StaleBackoff_FailedRelocationRetainsCandidateForBackoffRevalidation()
    {
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(10), readerFactory: fact, timeProvider: tp);

        Assert.True(svc.Read().IsUsable);
        tp.Advance(TimeSpan.FromMilliseconds(500));
        cat.MarkEnumerationIncomplete();

        ScannerReadResult failedRelocation = svc.Read();
        Assert.Equal(ReaderFailureCode.QueryFailure, failedRelocation.FailureCode);
        Assert.Equal(2, failedRelocation.Metrics.FullScanCount);

        tp.Advance(TimeSpan.FromMilliseconds(10));
        ScannerReadResult backedOff = svc.Read();

        Assert.Equal(ProviderHealth.Stale, backedOff.ReadResult.TransportHealth);
        Assert.Equal(ReaderFailureCode.StaleTelemetry, backedOff.FailureCode);
        Assert.Equal(2, backedOff.Metrics.FullScanCount);
        Assert.Equal(1, backedOff.Metrics.CacheHitCount);
    }

    [Fact]
    public void StaleBackoff_ResetsOnReattach()
    {
        var cat = new FakeMemoryCatalog();
        Guid sid = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: sid, seqA: 1, seqB: 2);
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        var tp = new ControllableTimeProvider();
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromMilliseconds(10), readerFactory: fact, timeProvider: tp);

        // First read healthy
        Assert.True(svc.Read().IsUsable);

        // Trigger stale + relocation → backoff active
        tp.Advance(TimeSpan.FromMilliseconds(500));
        var r2 = svc.Read(); // stale, relocation scan #2
        Assert.False(r2.IsUsable);

        // Kill the reader (not the process registration) so reattach succeeds
        fact.GetLastReader(42)!.Kill();
        var r3 = svc.Read(); // fresh attach, full scan #3
        Assert.True(r3.IsUsable, r3.ReadResult.FailureDetail ?? r3.FailureCode.ToString());
        Assert.Equal(2, r3.AttachmentGeneration);
        Assert.True(r3.Metrics.FullScanCount >= 2,
            $"Expected at least 2 scans (initial + fresh attach), got {r3.Metrics.FullScanCount}");
    }

    // ── CandidateLimitHits metric accuracy ──────────────────────

    [Fact]
    public void CandidateLimitHits_OnlyIncrementedForLimitExceeded()
    {
        // Incomplete enumeration (QueryFailure) must NOT increment CandidateLimitHits
        var cat = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(cat, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        cat.MarkEnumerationIncomplete();
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact);

        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.QueryFailure, r.FailureCode);
        Assert.Equal(0, r.Metrics.CandidateLimitHits);
    }

    [Fact]
    public void CandidateLimitHits_IncrementedForMaxCandidatesExceeded()
    {
        var cat = new FakeMemoryCatalog();
        for (int i = 0; i < 5; i++)
            ScannerTestHelpers.PlaceV5Region(cat, 0x10000 + (i * 0x100000),
                sessionId: Guid.NewGuid(), seqA: (uint)(i * 2 + 1), seqB: (uint)(i * 2 + 2));
        var fact = new FakeMemoryReaderFactory();
        fact.RegisterProcess(42, cat);
        using var svc = new V5ScannerService(new ProcessSelector { ProcessId = 42 },
            TimeSpan.FromSeconds(5), readerFactory: fact,
            scannerOptions: new SentinelScannerOptions { MaxCandidates = 2 });

        var r = svc.Read();
        Assert.False(r.IsUsable);
        Assert.Equal(ReaderFailureCode.CandidateLimitExceeded, r.FailureCode);
        Assert.True(r.Metrics.CandidateLimitHits > 0,
            "CandidateLimitHits must increment when CandidateLimitExceeded");
    }
}

// ────────────────────────────────────────────────────────────────────
// Native error mapping
// ────────────────────────────────────────────────────────────────────

public sealed class NativeErrorMappingTests
{
    [Theory]
    [InlineData(5, ReaderFailureCode.AccessDenied)]   // ERROR_ACCESS_DENIED
    [InlineData(87, ReaderFailureCode.ProcessNotFound)] // ERROR_INVALID_PARAMETER
    [InlineData(0, ReaderFailureCode.OpenFailure)]
    [InlineData(2, ReaderFailureCode.OpenFailure)]     // ERROR_FILE_NOT_FOUND
    [InlineData(6, ReaderFailureCode.OpenFailure)]     // ERROR_INVALID_HANDLE
    public void MapWin32Error_MapsToExpectedCodes(int errorCode, ReaderFailureCode expected)
    {
        Assert.Equal(expected, NativeMethods.MapWin32Error(errorCode));
    }
}

public sealed class WindowsMemoryReaderRangeTests
{
    [Theory]
    [InlineData(100UL, 1, 100UL, 200UL, true)]
    [InlineData(200UL, 1, 100UL, 200UL, true)]
    [InlineData(200UL, 2, 100UL, 200UL, false)]
    [InlineData(99UL, 1, 100UL, 200UL, false)]
    [InlineData(201UL, 0, 100UL, 200UL, false)]
    public void IsRangeWithinApplicationBounds_UsesInclusiveMaximum(
        ulong address,
        int size,
        ulong minimum,
        ulong maximum,
        bool expected)
    {
        bool actual = WindowsMemoryReader.IsRangeWithinApplicationBounds(
            (nuint)address, size, (nuint)minimum, (nuint)maximum);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsRangeWithinApplicationBounds_DoesNotOverflowAtNativeMaximum()
    {
        Assert.True(WindowsMemoryReader.IsRangeWithinApplicationBounds(
            nuint.MaxValue, 1, 0, nuint.MaxValue));
        Assert.False(WindowsMemoryReader.IsRangeWithinApplicationBounds(
            nuint.MaxValue, 2, 0, nuint.MaxValue));
    }
}
