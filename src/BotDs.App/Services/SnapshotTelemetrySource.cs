using BotDs.Core;

namespace BotDs.App.Services;

/// <summary>
/// M2 transport-neutral source: exposes the latest assembled snapshot from
/// <see cref="SnapshotPublisher"/> without Reader or calling-specific logic.
/// </summary>
public sealed class SnapshotTelemetrySource : ITelemetrySource
{
    private readonly SnapshotPublisher _publisher;

    public SnapshotTelemetrySource(SnapshotPublisher publisher)
    {
        _publisher = publisher;
    }

    public TelemetryFrame Current => _publisher.Latest;
}
