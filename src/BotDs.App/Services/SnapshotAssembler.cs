using BotDs.Core;
using BotDs.Reader;
using BotDs.Reader.V5;

namespace BotDs.App.Services;

/// <summary>
/// Heartbeat-aware snapshot assembler. Preserves game-state sections across
/// heartbeats within the same session and source generation. Clears carried
/// state on session change, source fault, process exit, or non-heartbeat
/// frames with no game-state sections.
///
/// Extracted from TelemetryReaderLoop for independent testability.
/// </summary>
internal sealed class SnapshotAssembler
{
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    private long _sourceGeneration;
    private Guid _lastSessionId;
    private TelemetryFrame _lastFullFrame = TelemetryFrame.Empty(DateTimeOffset.MinValue);
    private long _lastGameStateEvidenceTimestamp;

    public SnapshotAssembler(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Assemble a normalized TelemetryFrame from a scanner result, applying
    /// heartbeat-aware state carry and fault-clear rules.
    /// </summary>
    public TelemetryFrame Assemble(ScannerReadResult result, DateTimeOffset receivedAt)
    {
        lock (_lock)
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
                gameStateEvidenceAge = _timeProvider.GetElapsedTime(_lastGameStateEvidenceTimestamp);
            }

            // Map the raw scanner result to a TelemetryFrame
            TelemetryFrame frame = V5HealthMapper.ToTelemetryFrame(
                result.ReadResult, receivedAt, _sourceGeneration, gameStateEvidenceAge);

            if (hasGameState && frame.Player is not null)
            {
                _lastFullFrame = frame;
                _lastGameStateEvidenceTimestamp = _timeProvider.GetTimestamp();
            }
            else if (isHeartbeat && _lastFullFrame.Player is not null)
            {
                frame = _lastFullFrame with { Provider = frame.Provider };
            }

            return frame;
        }
    }
}
