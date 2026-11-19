using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner;

/// <summary>Owns the socket, heartbeat tasks and send gate for a single authenticated session.</summary>
internal sealed class RunnerConnection : IAsyncDisposable
{
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private readonly WebSocket _socket;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RunnerHeartbeatState _heartbeat;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _infoSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _pulseTask;
    private readonly Task _monitorTask;
    private int _closed;
    private int _disposed;

    public RunnerConnection(WebSocket socket, Guid processInstanceId, TimeProvider time, ILogger logger)
    {
        _socket = socket;
        _time = time;
        _logger = logger;
        _heartbeat = new RunnerHeartbeatState(processInstanceId, time);
        _pulseTask = PulseAsync();
        _monitorTask = MonitorAsync();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public long LastAcknowledgedSequence => _heartbeat.LastAcknowledgedSequence;
    public bool IsAlive => Volatile.Read(ref _closed) == 0 && _socket.State == WebSocketState.Open && _heartbeat.IsAlive;
    public bool CanSendApplication => _infoSent.Task.IsCompletedSuccessfully && IsAlive;

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) =>
        _socket.ReceiveAsync(buffer, token);

    public bool Acknowledge(MessageHeartbeatAck acknowledgement)
    {
        if (!IsAlive)
        {
            Abort("Heartbeat acknowledgement arrived after the session expired");
            return false;
        }

        return _heartbeat.Acknowledge(acknowledgement);
    }

    public Task<bool> SendInfoAsync(MessageInfo info) => SendAsync(Outbox.Frame(MessageType.Info, info), isInfo: true);

    public Task<bool> WriteAsync(Message message) => SendAsync(message);

    private async Task<bool> SendAsync(Message? message, bool isInfo = false, bool isPulse = false)
    {
        var entered = false;
        try
        {
            using var timeout = new CancellationTokenSource(SendTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _lifetime.Token);
            // One deadline covers both waiting for the gate and writing to the socket.
            await _sendGate.WaitAsync(linked.Token);
            entered = true;
            if (!IsAlive || (!isInfo && !_infoSent.Task.IsCompletedSuccessfully))
            {
                return false;
            }

            if (isPulse)
            {
                var pulse = _heartbeat.BeginSend();
                if (pulse is null)
                {
                    Abort("Heartbeat sequence exhausted or session expired");
                    return false;
                }

                message = Outbox.Frame(MessageType.Heartbeat, pulse);
            }

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message!, JsonDefaults.Options));
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, linked.Token);
            if (isPulse && !_heartbeat.CompleteSend())
            {
                Abort("Session expired while sending heartbeat");
                return false;
            }

            if (isInfo)
            {
                _infoSent.TrySetResult();
            }

            return IsAlive;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            Abort("WebSocket send failed or exceeded its deadline", ex);
            return false;
        }
        finally
        {
            if (entered)
            {
                _sendGate.Release();
            }
        }
    }

    private async Task PulseAsync()
    {
        try
        {
            await _infoSent.Task.WaitAsync(_lifetime.Token);
            using var cadence = new PeriodicTimer(RunnerHeartbeatState.Interval, _time);
            do
            {
                if (!await SendAsync(null, isPulse: true))
                {
                    Abort("Heartbeat could not be sent");
                    return;
                }
            }
            while (await cadence.WaitForNextTickAsync(_lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The owning session ended.
        }
        catch (Exception ex)
        {
            Abort("Heartbeat loop failed", ex);
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(RunnerHeartbeatState.MonitorInterval, _time);
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                if (!IsAlive)
                {
                    Abort("No valid heartbeat acknowledgement within 45 seconds");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The owning session ended.
        }
        catch (Exception ex)
        {
            Abort("Heartbeat monitor failed", ex);
        }
    }

    public void Abort(string reason, Exception? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _heartbeat.Stop();
        _logger.LogWarning(error, "Runner session {ConnectionId} ended: {Reason}", Id, reason);
        _socket.Abort();
        _lifetime.Cancel();
    }

    public async Task CloseAsync(CancellationToken token)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _heartbeat.Stop();
        _lifetime.Cancel();
        var entered = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _sendGate.WaitAsync(timeout.Token);
            entered = true;
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Runner stopping", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Closing runner session {ConnectionId} failed", Id);
            _socket.Abort();
        }
        finally
        {
            if (entered)
            {
                _sendGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Abort("Session disposed");
        await Task.WhenAll(_pulseTask, _monitorTask);
        // A writer can still be finishing after Abort. Keep its managed gate alive until it releases it.
        _socket.Dispose();
        _lifetime.Dispose();
    }
}
