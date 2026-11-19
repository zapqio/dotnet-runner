using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public sealed class RunnerConnectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    private sealed class Socket : WebSocket
    {
        private int _state = (int)WebSocketState.Open;
        private int _writing;
        public Channel<Message> Sent { get; } = Channel.CreateUnbounded<Message>();
        public Func<Message, CancellationToken, Task>? BeforeSend { get; set; }
        public Action<Message>? OnSent { get; set; }
        public int MaxConcurrentWrites { get; private set; }
        public override WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() => Volatile.Write(ref _state, (int)WebSocketState.Aborted);
        public override void Dispose() => Volatile.Write(ref _state, (int)WebSocketState.Closed);
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
        {
            Volatile.Write(ref _state, (int)WebSocketState.Closed);
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => CloseAsync(status, description, token);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            var count = Interlocked.Increment(ref _writing);
            MaxConcurrentWrites = Math.Max(MaxConcurrentWrites, count);
            try
            {
                var message = JsonSerializer.Deserialize<Message>(buffer.AsSpan(), JsonDefaults.Options)!;
                if (BeforeSend is { } beforeSend)
                {
                    await beforeSend(message, token);
                }

                token.ThrowIfCancellationRequested();
                OnSent?.Invoke(message);
                await Sent.Writer.WriteAsync(message, token);
            }
            finally
            {
                Interlocked.Decrement(ref _writing);
            }
        }
    }

    private static MessageInfo Info(Guid process) => new() { Name = "test", ProcessInstanceId = process, ActiveAttemptIds = [], Methods = [] };

    [Fact]
    public async Task Info_precedes_pulses_and_pulses_bypass_the_outbox()
    {
        var clock = new HeartbeatClock();
        var process = Guid.NewGuid();
        var socket = new Socket();
        await using var connection = new RunnerConnection(socket, process, clock, NullLogger.Instance);
        Assert.False(connection.CanSendApplication);
        Assert.False(await connection.WriteAsync(new Message { Type = MessageType.Job, Data = null! }));
        Assert.True(await connection.SendInfoAsync(Info(process)));
        Assert.Equal(MessageType.Info, (await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline)).Type);
        var first = await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(MessageType.Heartbeat, first.Type);
        var pulse = JsonSerializer.Deserialize<MessageHeartbeat>(first.Data, JsonDefaults.Options)!;
        Assert.Equal(1, pulse.Sequence);
        Assert.Equal(process, pulse.ProcessInstanceId);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(2, JsonSerializer.Deserialize<MessageHeartbeat>(second.Data, JsonDefaults.Options)!.Sequence);
        Assert.Equal(1, socket.MaxConcurrentWrites);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_info_or_missing_ack_expires_and_aborts_the_socket(bool sendInfo)
    {
        var clock = new HeartbeatClock();
        var process = Guid.NewGuid();
        var socket = new Socket();
        await using var connection = new RunnerConnection(socket, process, clock, NullLogger.Instance);
        if (sendInfo)
        {
            await connection.SendInfoAsync(Info(process));
        }

        clock.Advance(TimeSpan.FromSeconds(45));
        await EventuallyAsync(() => socket.State == WebSocketState.Aborted);
        Assert.False(connection.IsAlive);
        Assert.False(connection.Acknowledge(new MessageHeartbeatAck { ProcessInstanceId = process, Sequence = 1 }));
    }

    [Fact]
    public async Task Blocked_write_and_gate_wait_both_finish_within_the_send_deadline()
    {
        var clock = new HeartbeatClock();
        var process = Guid.NewGuid();
        var socket = new Socket();
        await using var connection = new RunnerConnection(socket, process, clock, NullLogger.Instance);
        await connection.SendInfoAsync(Info(process));
        await socket.Sent.Reader.ReadAsync();
        await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.BeforeSend = async (_, token) =>
        {
            blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var first = connection.WriteAsync(new Message { Type = MessageType.Log, Data = "{}" });
        await blocked.Task.WaitAsync(Deadline);
        var waiting = connection.WriteAsync(new Message { Type = MessageType.JobReturn, Data = "{}" });
        clock.Advance(RunnerConnection.SendTimeout);
        Assert.False(await first.WaitAsync(Deadline));
        Assert.False(await waiting.WaitAsync(Deadline));
        Assert.Equal(WebSocketState.Aborted, socket.State);
        Assert.Equal(1, socket.MaxConcurrentWrites);
    }

    [Fact]
    public async Task Old_session_ack_and_disposal_cannot_touch_a_new_session_of_the_same_process()
    {
        var clock = new HeartbeatClock();
        var process = Guid.NewGuid();
        var oldSocket = new Socket();
        var old = new RunnerConnection(oldSocket, process, clock, NullLogger.Instance);
        await old.SendInfoAsync(Info(process));
        await oldSocket.Sent.Reader.ReadAsync();
        await oldSocket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        old.Abort("test disconnect");
        var socket = new Socket();
        await using var current = new RunnerConnection(socket, process, clock, NullLogger.Instance);
        await current.SendInfoAsync(Info(process));
        await socket.Sent.Reader.ReadAsync();
        var first = await socket.Sent.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        Assert.Equal(1, JsonSerializer.Deserialize<MessageHeartbeat>(first.Data, JsonDefaults.Options)!.Sequence);
        Assert.NotEqual(old.Id, current.Id);
        clock.Advance(TimeSpan.FromSeconds(40));
        Assert.False(old.Acknowledge(new MessageHeartbeatAck { ProcessInstanceId = process, Sequence = 1 }));
        await old.DisposeAsync();
        Assert.Equal(WebSocketState.Open, socket.State);
        clock.Advance(TimeSpan.FromSeconds(5));
        await EventuallyAsync(() => socket.State == WebSocketState.Aborted);
    }

    [Fact]
    public async Task Fast_ack_before_send_continuation_is_applied_after_success()
    {
        var clock = new HeartbeatClock();
        var process = Guid.NewGuid();
        var socket = new Socket();
        await using var connection = new RunnerConnection(socket, process, clock, NullLogger.Instance);
        socket.OnSent = message =>
        {
            if (message.Type == MessageType.Heartbeat)
            {
                var pulse = JsonSerializer.Deserialize<MessageHeartbeat>(message.Data, JsonDefaults.Options)!;
                connection.Acknowledge(new MessageHeartbeatAck { ProcessInstanceId = pulse.ProcessInstanceId, Sequence = pulse.Sequence });
            }
        };
        await connection.SendInfoAsync(Info(process));
        await EventuallyAsync(() => connection.LastAcknowledgedSequence == 1);
        for (var n = 2; n <= 8; n++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await EventuallyAsync(() => connection.LastAcknowledgedSequence == n);
        }

        Assert.True(connection.IsAlive);
    }

    internal static async Task EventuallyAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(Deadline);
        while (!condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
