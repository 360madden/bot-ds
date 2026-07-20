using System.Collections.ObjectModel;
using System.Diagnostics;
using BotDs.App.Services;
using BotDs.Core;

namespace BotDs.Tests;

public sealed class SnapshotPublisherTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset initialUtc)
        {
            _utcNow = initialUtc;
            _timestamp = 0;
        }

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _timestamp += (long)(duration.TotalSeconds * Stopwatch.Frequency);
            _utcNow += duration;
        }

        public void AdvanceMonotonicOnly(TimeSpan duration)
        {
            _timestamp += (long)(duration.TotalSeconds * Stopwatch.Frequency);
        }

        public void SetUtcNow(DateTimeOffset utc)
        {
            _utcNow = utc;
        }
    }

    [Fact]
    public void Publish_resets_age_accumulation()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.Zero));
        time.Advance(TimeSpan.FromSeconds(10));

        // Publish a second frame with Age = 2 s.
        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.FromSeconds(2)));
        time.Advance(TimeSpan.FromSeconds(3));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(TimeSpan.FromSeconds(5), latest.Provider.Age);
    }

    [Fact]
    public void Latest_ages_frame_by_elapsed_monotonic_time()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(4));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(TimeSpan.FromSeconds(5), latest.Provider.Age);
    }

    [Fact]
    public void Age_never_decreases_when_wall_clock_rolls_backward()
    {
        DateTimeOffset utc = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.Zero));
        time.Advance(TimeSpan.FromSeconds(5));

        TelemetryFrame first = pub.Latest;
        Assert.Equal(TimeSpan.FromSeconds(5), first.Provider.Age);

        // Wall clock rolls back 10 hours, but monotonic time advances 1 s.
        time.SetUtcNow(utc - TimeSpan.FromHours(10));
        time.AdvanceMonotonicOnly(TimeSpan.FromSeconds(1));

        TelemetryFrame second = pub.Latest;
        // Base Age 0 + 6 s monotonic = 6 s, which is >= 5 s.
        Assert.Equal(TimeSpan.FromSeconds(6), second.Provider.Age);
        Assert.True(second.Provider.Age >= first.Provider.Age);
    }

    [Fact]
    public void Age_saturates_on_overflow()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        TimeSpan baseAge = TimeSpan.MaxValue - TimeSpan.FromSeconds(1);
        pub.Publish(CreateFrame(ProviderHealth.Healthy, baseAge));
        time.Advance(TimeSpan.FromSeconds(2));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(TimeSpan.MaxValue, latest.Provider.Age);
    }

    [Fact]
    public void ReceivedAtUtc_is_set_to_current_utc_not_publish_time()
    {
        DateTimeOffset utc = new(2025, 6, 15, 10, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(3));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(utc + TimeSpan.FromSeconds(3), latest.Provider.ReceivedAtUtc);
    }

    [Fact]
    public void Constructor_default_uses_system_time_provider()
    {
        SnapshotPublisher pub = new();
        TelemetryFrame latest = pub.Latest;
        Assert.Equal(ProviderHealth.Disconnected, latest.Provider.Health);
        Assert.Equal(TimeSpan.MaxValue, latest.Provider.Age);
    }

    [Fact]
    public void Empty_initial_frame_has_disconnected_health()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(ProviderHealth.Disconnected, latest.Provider.Health);
        Assert.Equal(TimeSpan.MaxValue, latest.Provider.Age);
    }

    [Fact]
    public void Disconnected_frame_ages_normally()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Disconnected, TimeSpan.FromSeconds(10)));
        time.Advance(TimeSpan.FromSeconds(5));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal(TimeSpan.FromSeconds(15), latest.Provider.Age);
    }

    [Fact]
    public void Provider_fields_other_than_age_and_received_at_are_preserved()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(2));

        TelemetryFrame latest = pub.Latest;
        Assert.Equal("v5", latest.Provider.ProtocolVersion);
        Assert.Equal("test-session", latest.Provider.SessionId);
        Assert.Equal((ulong)42, latest.Provider.Sequence);
        Assert.Equal(ProviderHealth.Healthy, latest.Provider.Health);
    }

    [Fact]
    public void Multiple_calls_to_latest_are_consistent()
    {
        DateTimeOffset utc = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        FakeTimeProvider time = new(utc);
        SnapshotPublisher pub = new(time);

        pub.Publish(CreateFrame(ProviderHealth.Healthy, TimeSpan.FromSeconds(1)));
        time.Advance(TimeSpan.FromSeconds(3));

        TelemetryFrame first = pub.Latest;
        TelemetryFrame second = pub.Latest;

        Assert.Equal(first.Provider.Age, second.Provider.Age);
        Assert.Equal(first.Provider.ReceivedAtUtc, second.Provider.ReceivedAtUtc);
    }

    private static TelemetryFrame CreateFrame(ProviderHealth health, TimeSpan age)
    {
        return new TelemetryFrame(
            new ProviderStatus(
                health,
                "v5",
                "test-session",
                42,
                0,
                DateTimeOffset.UtcNow,
                age),
            null,
            null,
            ReadOnlyDictionary<string, AbilityState>.Empty,
            [],
            []);
    }
}
