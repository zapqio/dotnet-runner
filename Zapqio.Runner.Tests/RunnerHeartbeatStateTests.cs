using Zapqio.Runner.Protocol;

namespace Zapqio.Runner.Tests;

public sealed class RunnerHeartbeatStateTests
{
    private readonly HeartbeatClock _clock = new();
    private readonly Guid _process = Guid.NewGuid();
    private MessageHeartbeatAck Ack(long sequence) => new() { ProcessInstanceId = _process, Sequence = sequence };

    [Fact]
    public void No_ack_expires_at_45_seconds_even_if_pulses_were_sent()
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        for (var n = 1; n <= 5; n++)
        {
            Assert.Equal(n, state.BeginSend()!.Sequence);
            Assert.True(state.CompleteSend());
            _clock.Advance(TimeSpan.FromSeconds(n == 5 ? 4 : 10));
        }

        Assert.True(state.IsAlive);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(state.Acknowledge(Ack(5)));
        Assert.False(state.IsAlive);
        Assert.Null(state.BeginSend());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(9007199254740992L)]
    public void Unsent_or_invalid_sequence_does_not_renew_liveness(long invalid)
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        state.BeginSend();
        state.CompleteSend();
        _clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(state.Acknowledge(Ack(invalid)));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(state.IsAlive);
    }

    [Fact]
    public void Duplicate_old_and_foreign_process_acknowledgements_do_not_extend_the_deadline()
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        state.BeginSend();
        state.CompleteSend();
        Assert.True(state.Acknowledge(Ack(1)));
        state.BeginSend();
        state.CompleteSend();
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(state.Acknowledge(Ack(2)));
        _clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(state.Acknowledge(Ack(1)));
        Assert.False(state.Acknowledge(Ack(2)));
        Assert.False(state.Acknowledge(Ack(2) with { ProcessInstanceId = Guid.NewGuid() }));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(state.IsAlive);
    }

    [Fact]
    public void Early_ack_is_deferred_until_send_succeeds_and_preserves_receive_time()
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        _clock.Advance(TimeSpan.FromSeconds(10));
        state.BeginSend();
        Assert.False(state.Acknowledge(Ack(1)));
        Assert.Equal(0, state.LastAcknowledgedSequence);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.True(state.CompleteSend());
        Assert.Equal(1, state.LastAcknowledgedSequence);
        _clock.Advance(TimeSpan.FromSeconds(42));
        Assert.False(state.IsAlive);
    }

    [Fact]
    public void Failed_send_cannot_be_rescued_by_an_early_ack()
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        state.BeginSend();
        state.Acknowledge(Ack(1));
        state.Stop();
        Assert.False(state.CompleteSend());
        Assert.False(state.Acknowledge(Ack(1)));
        Assert.Equal(0, state.LastAcknowledgedSequence);
    }

    [Fact]
    public void Clock_corrections_do_not_change_the_monotonic_timeout()
    {
        var state = new RunnerHeartbeatState(_process, _clock);
        _clock.JumpUtc(TimeSpan.FromDays(100));
        Assert.True(state.IsAlive);
        _clock.Advance(TimeSpan.FromSeconds(44));
        _clock.JumpUtc(TimeSpan.FromDays(-200));
        Assert.True(state.IsAlive);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(state.IsAlive);
    }
}
