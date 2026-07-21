using BotDs.App.Services;
using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;

namespace BotDs.Tests;

public sealed class SnapshotAssemblerTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Heartbeat_preserves_game_state_from_last_full_frame()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var fullFrame = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true);
        var heartbeat = CreateResult(session, seq: 2, heartbeat: true, hasPlayer: false);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f1 = assembler.Assemble(fullFrame, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f2 = assembler.Assemble(heartbeat, _time.GetUtcNow());

        Assert.Equal("TestPlayer", f2.Player!.Name);
        Assert.True(f2.Provider.IsHeartbeat);
    }

    [Fact]
    public void Session_change_clears_carried_state()
    {
        var assembler = new SnapshotAssembler(_time);

        var fullA = CreateResult(Guid.NewGuid(), seq: 1, heartbeat: false, hasPlayer: true);
        var hbB = CreateResult(Guid.NewGuid(), seq: 1, heartbeat: true, hasPlayer: false);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        assembler.Assemble(fullA, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f2 = assembler.Assemble(hbB, _time.GetUtcNow());

        Assert.Null(f2.Player);
    }

    [Fact]
    public void Source_fault_clears_carried_state()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var full = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true);
        var fault = ScannerReadResult.Failure(
            ProviderHealth.Faulted, ReaderFailureCode.CandidateInvalid,
            "test", 1234, 1, ScannerMetrics.Empty);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        assembler.Assemble(full, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f2 = assembler.Assemble(fault, _time.GetUtcNow());

        Assert.Null(f2.Player);
    }

    [Fact]
    public void Generation_change_clears_carried_state()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var gen1 = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true, generation: 1);
        var gen2 = CreateResult(session, seq: 2, heartbeat: true, hasPlayer: false, generation: 2);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        assembler.Assemble(gen1, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f2 = assembler.Assemble(gen2, _time.GetUtcNow());

        Assert.Null(f2.Player);
    }

    [Fact]
    public void Game_state_evidence_age_preserved_across_heartbeats()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var full = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true);
        var hb = CreateResult(session, seq: 2, heartbeat: true, hasPlayer: false);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        assembler.Assemble(full, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(200));
        TelemetryFrame f2 = assembler.Assemble(hb, _time.GetUtcNow());

        // Heartbeat carries the old game-state, so evidence age should be >= 200ms
        Assert.True(f2.Provider.GameStateEvidenceAge >= TimeSpan.FromMilliseconds(200));
        Assert.True(f2.Provider.IsHeartbeat);
    }

    [Fact]
    public void Consecutive_full_frames_update_game_state()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var f1 = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true, playerName: "One");
        var f2 = CreateResult(session, seq: 2, heartbeat: false, hasPlayer: true, playerName: "Two");

        _time.Advance(TimeSpan.FromMilliseconds(50));
        assembler.Assemble(f1, _time.GetUtcNow());

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame result = assembler.Assemble(f2, _time.GetUtcNow());

        Assert.Equal("Two", result.Player!.Name);
        Assert.False(result.Provider.IsHeartbeat);
        Assert.True(result.Provider.GameStateEvidenceAge < TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Scanner_attachment_pid_propagates_to_assembled_frame_provider()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var result = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true, attachmentPid: 8888);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame frame = assembler.Assemble(result, _time.GetUtcNow());

        Assert.Equal(8888, frame.Provider.AttachmentProcessId);
    }

    [Fact]
    public void Faulted_frame_with_pid_does_not_preserve_previous_game_state()
    {
        var assembler = new SnapshotAssembler(_time);
        var session = Guid.NewGuid();

        var full = CreateResult(session, seq: 1, heartbeat: false, hasPlayer: true, attachmentPid: 1111);
        var fault = ScannerReadResult.Failure(
            ProviderHealth.Faulted, ReaderFailureCode.CandidateInvalid,
            "test", 2222, 1, ScannerMetrics.Empty);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f1 = assembler.Assemble(full, _time.GetUtcNow());
        Assert.Equal(1111, f1.Provider.AttachmentProcessId);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        TelemetryFrame f2 = assembler.Assemble(fault, _time.GetUtcNow());

        Assert.Null(f2.Player);
        Assert.Equal(2222, f2.Provider.AttachmentProcessId);
    }

    // ── Helpers ────────────────────────────────────────────────

    private static ScannerReadResult CreateResult(
        Guid session, uint seq, bool heartbeat, bool hasPlayer,
        long generation = 1, string? playerName = null, int attachmentPid = 1234)
    {
        var frame = CreateParsedFrame(session, seq, heartbeat,
            hasPlayer ? (playerName ?? "TestPlayer") : null);
        return ScannerReadResult.Healthy(
            StableReadResult.Healthy(frame), attachmentPid, generation, ScannerMetrics.Empty);
    }

    private static ParsedV5Frame CreateParsedFrame(
        Guid session, uint seq, bool heartbeat, string? playerName)
    {
        byte flags = (byte)(heartbeat ? V5Constants.FlagIsHeartbeat : 0);
        uint sectionsMask = heartbeat
            ? V5Constants.MaskProviderInfo
            : V5Constants.MaskProviderInfo | V5Constants.MaskPlayer;

        var header = new V5BufferHeader
        {
            Sequence = seq,
            ProducerFrameMs = (uint)(seq * 50),
            SectionsMask = sectionsMask,
            HeartbeatIntervalMs = 50,
            PayloadLength = 0,
            ProtocolVersion = 5,
            Flags = flags,
        };

        var provider = new ParsedProviderInfo(session, header.ProducerFrameMs, 500, "", 1);

        ParsedUnitState? player = playerName is not null
            ? new ParsedUnitState("player1", playerName, 45, "warrior",
                0x07, 1, 5000, 5000, 100, 100, "power", null, null, -1, -1, 0)
            : null;

        return new ParsedV5Frame(header, provider, player, null,
            Array.Empty<ParsedAbilityState>(), Array.Empty<ParsedAuraState>(),
            Array.Empty<ParsedAuraState>(), 0);
    }
}

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan delta)
    {
        _now += delta;
        _timestamp += delta.Ticks;
    }
}
