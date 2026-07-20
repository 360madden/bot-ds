using System.Threading;
using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;

namespace BotDs.App.Services;

/// <summary>
/// Hosted background service that periodically reads the V5 scanner and publishes
/// normalized TelemetryFrames to the SnapshotPublisher. This is the bridge between
/// the external process-memory Reader and the in-process combat/dashboard pipeline.
/// </summary>
public sealed class TelemetryReaderLoop : BackgroundService
{
    private readonly V5ScannerService _scanner;
    private readonly SnapshotPublisher _publisher;
    private readonly ILogger<TelemetryReaderLoop> _log;
    private readonly TimeSpan _readInterval;
    private readonly TimeSpan _maxTelemetryAge;
    private readonly TimeProvider _timeProvider;

    // Expose scanner health for dashboard consumption
    private volatile ScannerReadResult? _lastResult;
    private volatile ScannerMetrics _metrics = ScannerMetrics.Empty;
    private readonly object _resultLock = new();
    private int _consecutiveFaults;
    private int _successfulReadCount;

    // Snapshot assembler state: carries game-state across heartbeats within
    // the same source generation and session, clears on fault/boundary.
    private long _sourceGeneration;
    private Guid _lastSessionId;
    private TelemetryFrame _lastFullFrame = TelemetryFrame.Empty(DateTimeOffset.MinValue);
    private long _lastGameStateEvidenceTimestamp;
    private readonly object _assemblerLock = new();

    public TelemetryReaderLoop(
        V5ScannerService scanner,
        SnapshotPublisher publisher,
        IConfiguration configuration,
        ILogger<TelemetryReaderLoop> log,
        TimeProvider? timeProvider = null)
    {
        _scanner = scanner;
        _publisher = publisher;
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _readInterval = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Scanner:ReadIntervalMs", 50));
        _maxTelemetryAge = TimeSpan.FromMilliseconds(
            configuration.GetValue<int>("BotDs:Evaluator:MaximumTelemetryAgeMs", 5000));
    }

    /// <summary>Latest raw scanner result for dashboard/metrics consumption.</summary>
    public ScannerReadResult? LastResult
    {
        get { lock (_resultLock) return _lastResult; }
    }

    /// <summary>Current scanner metrics (cache hits, scans, failures, etc).</summary>
    public ScannerMetrics Metrics => _metrics;

    /// <summary>Current attachment PID, or 0 if not attached.</summary>
    public int AttachmentPid => _scanner.AttachmentPid;

    /// <summary>Monotonic attachment generation.</summary>
    public long AttachmentGeneration => _scanner.AttachmentGeneration;

    /// <summary>
    /// Heartbeat-aware snapshot assembly. Preserves game-state sections across
    /// heartbeats within the same session and source generation. Clears carried
    /// state on session change, source fault, process exit, or non-heartbeat
    /// frames with no game-state sections.
    /// </summary>
    private TelemetryFrame AssembleSnapshot(ScannerReadResult result, DateTimeOffset receivedAt)
    {
        lock (_assemblerLock)
        {
            bool isFault = !result.IsUsable;
            bool isHeartbeat = result.Frame?.Header.IsHeartbeat ?? false;
            bool hasGameState = result.Frame is not null && !isHeartbeat;

            // Clear carried state on fault, generation change, or session change
            if (isFault || result.AttachmentGeneration != _sourceGeneration)
            {
                _lastFullFrame = TelemetryFrame.Empty(receivedAt);
                _lastGameStateEvidenceTimestamp = 0;
                _sourceGeneration = result.AttachmentGeneration;
                _lastSessionId = Guid.Empty;
            }

            Guid currentSession = result.Frame?.Provider?.SessionId ?? Guid.Empty;
            if (currentSession != Guid.Empty && currentSession != _lastSessionId)
            {
                _lastFullFrame = TelemetryFrame.Empty(receivedAt);
                _lastGameStateEvidenceTimestamp = 0;
                _lastSessionId = currentSession;
            }

            // Compute game-state evidence age
            TimeSpan gameStateEvidenceAge = TimeSpan.MaxValue;
            if (_lastGameStateEvidenceTimestamp > 0)
            {
                long now = _timeProvider.GetTimestamp();
                gameStateEvidenceAge = _timeProvider.GetElapsedTime(_lastGameStateEvidenceTimestamp);
            }

            // Map the raw scanner result to a TelemetryFrame
            TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(
                result.ReadResult, receivedAt, _sourceGeneration, gameStateEvidenceAge);

            if (hasGameState && frame.Player is not null)
            {
                // Full frame with game state — update carried state and evidence timestamp
                _lastFullFrame = frame;
                _lastGameStateEvidenceTimestamp = _timeProvider.GetTimestamp();
            }
            else if (isHeartbeat && _lastFullFrame.Player is not null)
            {
                // Heartbeat: preserve game-state from last full frame,
                // update provider status only (transport liveness)
                frame = _lastFullFrame with
                {
                    Provider = frame.Provider,
                };
            }
            // else: no game state and no carried state — return mapped frame as-is

            return frame;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "TelemetryReaderLoop started (interval={IntervalMs}ms, pid={Pid})",
            _readInterval.TotalMilliseconds,
            _scanner.AttachmentPid);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset receivedAt = _timeProvider.GetUtcNow();
                ScannerReadResult result = _scanner.Read(stoppingToken);

                lock (_resultLock)
                {
                    _lastResult = result;
                    _metrics = result.Metrics;
                }

                // Assemble snapshot with heartbeat-aware state carry
                TelemetryFrame frame = AssembleSnapshot(result, receivedAt);
                _publisher.Publish(frame);

                if (result.IsUsable)
                {
                    _consecutiveFaults = 0;
                    _successfulReadCount++;
                    var f = result.Frame;
                    _log.LogDebug(
                        "Scanner read: seq={Seq}, health={Health}, player={HasPlayer}, target={HasTarget}, abilities={AbilCount}, cacheHit={CacheHit}",
                        f?.Header.Sequence ?? 0,
                        result.ReadResult.TransportHealth,
                        f?.Player is not null,
                        f?.Target is not null,
                        f?.Abilities.Count ?? 0,
                        _metrics.CacheHitCount > 0);

                    // Log session info on first few successful reads only
                    if (f?.Provider is not null && _successfulReadCount <= 3)
                    {
                        _log.LogInformation(
                            "Live telemetry established: session={Session}, seq={Seq}, version={Version}",
                            f.Provider.SessionId.ToString("D")[..8],
                            f.Header.Sequence,
                            f.Provider.ClientVersion);
                    }
                }
                else
                {
                    int faults = Interlocked.Increment(ref _consecutiveFaults);
                    if (faults <= 1 || faults % 200 == 0)
                    {
                        _log.LogWarning(
                            "Scanner read fault (x{Count}): health={Health}, code={Code}, detail={Detail}",
                            faults,
                            result.ReadResult.TransportHealth,
                            result.FailureCode,
                            result.ReadResult.FailureDetail ?? "—");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "TelemetryReaderLoop faulted on read cycle");
            }

            await Task.Delay(_readInterval, stoppingToken);
        }

        _log.LogInformation("TelemetryReaderLoop stopped");
    }
}
