namespace Zapqio.Runner.Tests;

/// <summary>Virtual monotonic time and timers; UTC can jump without affecting deadlines.</summary>
internal sealed class HeartbeatClock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private long _timestamp;
    private DateTimeOffset _utc = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utc;
        }
    }

    public void JumpUtc(TimeSpan delta)
    {
        lock (_gate)
        {
            _utc += delta;
        }
    }

    public void Advance(TimeSpan delta)
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            _timestamp += delta.Ticks;
            _utc += delta;
            due = _timers.Where(timer => timer.DueAt <= _timestamp).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = timer.PeriodTicks > 0 ? _timestamp + timer.PeriodTicks : long.MaxValue;
            }
        }

        foreach (var timer in due)
        {
            timer.Callback(timer.State);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ClockTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Change(dueTime, period);
        }

        return timer;
    }

    private sealed class ClockTimer(HeartbeatClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public long DueAt { get; set; } = long.MaxValue;
        public long PeriodTicks { get; private set; }
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                DueAt = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._timestamp + dueTime.Ticks;
                PeriodTicks = period.Ticks;
                return true;
            }
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                DueAt = long.MaxValue;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
