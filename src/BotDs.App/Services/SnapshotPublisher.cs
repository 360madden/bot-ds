using BotDs.Core;

namespace BotDs.App.Services;

public sealed class SnapshotPublisher
{
    private readonly TimeProvider _timeProvider;
    private TelemetryFrame _latest;
    private long _publishTimestamp;
    private TimeSpan _baseAge;
    private readonly object _lock = new();

    public SnapshotPublisher()
        : this(TimeProvider.System)
    {
    }

    public SnapshotPublisher(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _baseAge = TimeSpan.MaxValue;
        _publishTimestamp = timeProvider.GetTimestamp();
        _latest = TelemetryFrame.Empty(timeProvider.GetUtcNow());
    }

    public TelemetryFrame Latest
    {
        get
        {
            lock (_lock)
            {
                return ApplyFreshness(_latest);
            }
        }
    }

    public void Publish(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            _latest = frame;
            _baseAge = frame.Provider.Age;
            _publishTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private TelemetryFrame ApplyFreshness(TelemetryFrame frame)
    {
        TimeSpan elapsedTs = _timeProvider.GetElapsedTime(_publishTimestamp);

        TimeSpan newAge;
        if (elapsedTs >= TimeSpan.Zero && _baseAge >= TimeSpan.MaxValue - elapsedTs)
        {
            newAge = TimeSpan.MaxValue;
        }
        else
        {
            newAge = _baseAge + elapsedTs;
        }

        DateTimeOffset nowUtc = _timeProvider.GetUtcNow();

        return frame with
        {
            Provider = frame.Provider with
            {
                Age = newAge,
                ReceivedAtUtc = nowUtc,
            },
        };
    }
}
