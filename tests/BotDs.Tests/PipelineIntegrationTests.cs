using BotDs.App.Services;
using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotDs.Tests;

/// <summary>
/// End-to-end pipeline integration tests: fake memory → V5ScannerService →
/// TelemetryReaderLoop → SnapshotPublisher.
/// Uses the proven ScannerTestHelpers.PlaceV5Region for V5 buffer setup.
/// </summary>
public sealed class PipelineIntegrationTests : IDisposable
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(500);
    private readonly List<V5ScannerService> _scanners = [];

    public void Dispose()
    {
        foreach (var s in _scanners)
            s.Dispose();
    }

    // ═══════════════════════════════════════════════════════════
    // Full pipeline: scanner → reader loop → publisher
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Pipeline_publishes_provider_info_from_v5_buffer()
    {
        var session = Guid.NewGuid();
        var scanner = CreateScanner(pid: 42, session, seqA: 1, seqB: 2);
        var (publisher, _) = await RunLoopAsync(scanner, delayMs: 200);

        TelemetryFrame frame = publisher.Latest;
        Assert.Equal(ProviderHealth.Healthy, frame.Provider.Health);
        Assert.Equal(session, Guid.Parse(frame.Provider.SessionId));
        Assert.True(frame.Provider.Sequence >= 1);
    }

    [Fact]
    public async Task Pipeline_handles_null_game_state()
    {
        // BuildSlot only includes ProviderInfo (no UnitState sections),
        // so player/target are null — verifies graceful null handling.
        var scanner = CreateScanner(pid: 42, Guid.NewGuid(), seqA: 1, seqB: 2);
        var (publisher, _) = await RunLoopAsync(scanner, delayMs: 200);

        TelemetryFrame frame = publisher.Latest;
        Assert.Equal(ProviderHealth.Healthy, frame.Provider.Health);
        Assert.Null(frame.Player);
        Assert.Null(frame.Target);
    }

    [Fact]
    public async Task Pipeline_fault_produces_disconnected_frame()
    {
        // Scanner with unregistered PID → immediate failure
        using var scanner = new V5ScannerService(
            new ProcessSelector { ProcessId = 99999 },
            MaxAge,
            readerFactory: new FakeMemoryReaderFactory());

        var (publisher, _) = await RunLoopAsync(scanner, delayMs: 100);

        TelemetryFrame frame = publisher.Latest;
        Assert.Null(frame.Player);
        Assert.Equal(ProviderHealth.Disconnected, frame.Provider.Health);
    }

    [Fact]
    public async Task Pipeline_attachment_info_exposed()
    {
        var scanner = CreateScanner(pid: 42, Guid.NewGuid(), seqA: 1, seqB: 2);
        var (_, loop) = await RunLoopAsync(scanner, delayMs: 200);

        Assert.Equal(42, loop.AttachmentPid);
        Assert.Equal(1, loop.AttachmentGeneration);
    }

    [Fact]
    public async Task Pipeline_metrics_available_after_reads()
    {
        var scanner = CreateScanner(pid: 42, Guid.NewGuid(), seqA: 1, seqB: 2);
        var (_, loop) = await RunLoopAsync(scanner, delayMs: 200);

        var metrics = loop.Metrics;
        Assert.True(metrics.FullScanCount > 0 || metrics.CacheHitCount > 0,
            $"Expected at least one read cycle. FullScan={metrics.FullScanCount}, CacheHit={metrics.CacheHitCount}");
    }

    [Fact]
    public async Task Pipeline_last_result_exposed()
    {
        var scanner = CreateScanner(pid: 42, Guid.NewGuid(), seqA: 1, seqB: 2);
        var (_, loop) = await RunLoopAsync(scanner, delayMs: 200);

        var lastResult = loop.LastResult;
        Assert.NotNull(lastResult);
        Assert.True(lastResult.IsUsable);
    }

    [Fact]
    public async Task Pipeline_process_exit_produces_disconnected()
    {
        var catalog = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(catalog, 0x1000000, sessionId: Guid.NewGuid(), seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, catalog);
        var scanner = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, MaxAge, readerFactory: factory);
        _scanners.Add(scanner);

        var publisher = new SnapshotPublisher();
        using var cts = new CancellationTokenSource();
        var config = CreateConfig(readIntervalMs: 10);

        var loop = new TelemetryReaderLoop(scanner, publisher, config,
            NullLogger<TelemetryReaderLoop>.Instance);

        var loopTask = loop.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        // Kill the reader AND prevent re-attachment (matching ScannerTests line 888)
        factory.GetLastReader(42)?.Kill();
        factory.KillProcess(42);

        await Task.Delay(200, CancellationToken.None);
        cts.Cancel();
        try { await loopTask; } catch (OperationCanceledException) { }

        // After process exit, the publisher should show disconnected
        TelemetryFrame frame = publisher.Latest;
        Assert.Equal(ProviderHealth.Disconnected, frame.Provider.Health);
    }

    [Fact]
    public async Task Pipeline_heartbeat_frame_preserves_provider_info()
    {
        var catalog = new FakeMemoryCatalog();
        var session = Guid.NewGuid();
        ScannerTestHelpers.PlaceV5Region(catalog, 0x1000000, sessionId: session, seqA: 1, seqB: 2);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(42, catalog);
        var scanner = new V5ScannerService(
            new ProcessSelector { ProcessId = 42 }, MaxAge, readerFactory: factory);
        _scanners.Add(scanner);

        var publisher = new SnapshotPublisher();
        using var cts = new CancellationTokenSource();
        var config = CreateConfig(readIntervalMs: 10);

        var loop = new TelemetryReaderLoop(scanner, publisher, config,
            NullLogger<TelemetryReaderLoop>.Instance);

        var loopTask = loop.StartAsync(cts.Token);
        await Task.Delay(200, CancellationToken.None);

        // Replace both buffer slots with heartbeat frames (same session/sequence).
        // Must recalculate CRC after modifying the flags byte.
        byte[] heartbeatSlot = ScannerTestHelpers.BuildSlot(2, session);
        heartbeatSlot[V5Constants.HdrFlagsOffset] |= V5Constants.FlagIsHeartbeat;
        uint payloadLength = BitConverter.ToUInt32(heartbeatSlot.AsSpan(V5Constants.HdrPayloadLengthOffset));
        V5Crc32.WriteCrc(heartbeatSlot.AsSpan(), payloadLength);
        catalog.ModifyPage(0x1000000, V5Constants.BufferAOffset, heartbeatSlot);
        catalog.ModifyPage(0x1000000, V5Constants.BufferBOffset, heartbeatSlot);

        await Task.Delay(200, CancellationToken.None);
        cts.Cancel();
        try { await loopTask; } catch (OperationCanceledException) { }

        // Heartbeat frames should not corrupt provider info
        TelemetryFrame frame = publisher.Latest;
        Assert.Equal(ProviderHealth.Healthy, frame.Provider.Health);
        Assert.Equal(session, Guid.Parse(frame.Provider.SessionId));
    }

    // ═══════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════

    private static IConfigurationRoot CreateConfig(int readIntervalMs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDs:Scanner:ReadIntervalMs"] = readIntervalMs.ToString(),
                ["BotDs:Evaluator:MaximumTelemetryAgeMs"] = "500",
            })
            .Build();

    private V5ScannerService CreateScanner(int pid, Guid session, uint seqA, uint seqB)
    {
        var catalog = new FakeMemoryCatalog();
        ScannerTestHelpers.PlaceV5Region(catalog, 0x1000000,
            sessionId: session, seqA: seqA, seqB: seqB);
        var factory = new FakeMemoryReaderFactory();
        factory.RegisterProcess(pid, catalog);
        var scanner = new V5ScannerService(
            new ProcessSelector { ProcessId = pid }, MaxAge, readerFactory: factory);
        _scanners.Add(scanner);
        return scanner;
    }

    /// <summary>
    /// Start the reader loop, let it run for <paramref name="delayMs"/>,
    /// then cancel and return publisher + loop for assertions.
    /// Uses try/finally to ensure cancellation even if the delay is interrupted.
    /// </summary>
    private static async Task<(SnapshotPublisher publisher, TelemetryReaderLoop loop)> RunLoopAsync(
        V5ScannerService scanner, int delayMs = 200)
    {
        var publisher = new SnapshotPublisher();
        using var cts = new CancellationTokenSource();
        var config = CreateConfig(readIntervalMs: 10);

        var loop = new TelemetryReaderLoop(scanner, publisher, config,
            NullLogger<TelemetryReaderLoop>.Instance);

        var loopTask = loop.StartAsync(cts.Token);
        try
        {
            await Task.Delay(delayMs, CancellationToken.None);
        }
        finally
        {
            cts.Cancel();
        }
        try { await loopTask; } catch (OperationCanceledException) { }

        return (publisher, loop);
    }
}
