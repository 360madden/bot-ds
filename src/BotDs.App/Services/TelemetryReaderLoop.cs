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
    private readonly SnapshotAssembler _assembler;
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
        _assembler = new SnapshotAssembler(_timeProvider);
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

    /// <summary>Delegate snapshot assembly to the extracted SnapshotAssembler.</summary>
    private TelemetryFrame AssembleSnapshot(ScannerReadResult result, DateTimeOffset receivedAt)
        => _assembler.Assemble(result, receivedAt);

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
