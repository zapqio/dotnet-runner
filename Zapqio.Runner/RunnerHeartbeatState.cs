using Zapqio.Runner.Protocol;

namespace Zapqio.Runner;

/// <summary>Monotonic liveness and sequence accounting, owned by exactly one connection.</summary>
internal sealed class RunnerHeartbeatState(Guid processInstanceId, TimeProvider time)
{
    public const long MaxSequence = 9007199254740991;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private long _lastAcknowledgedAt = time.GetTimestamp();
    private long _lastSent;
    private long _lastAcknowledged;
    private long _sending;
    private long? _earlyAcknowledgementAt;
    private bool _stopped;

    public long LastAcknowledgedSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastAcknowledged;
            }
        }
    }

    public bool IsAlive
    {
        get
        {
            lock (_gate)
            {
                return CheckAlive();
            }
        }
    }

    public MessageHeartbeat? BeginSend()
    {
        lock (_gate)
        {
            if (!CheckAlive() || _sending != 0 || _lastSent == MaxSequence)
            {
                return null;
            }

            _sending = _lastSent + 1;
            _earlyAcknowledgementAt = null;
            return new MessageHeartbeat { ProcessInstanceId = processInstanceId, Sequence = _sending };
        }
    }

    public bool CompleteSend()
    {
        lock (_gate)
        {
            if (!CheckAlive() || _sending == 0)
            {
                return false;
            }

            _lastSent = _sending;
            _sending = 0;
            // A fast peer may answer before SendAsync's continuation gets its turn.
            // Defer that ACK until the write succeeds; a queued pulse never renews liveness.
            if (_earlyAcknowledgementAt is { } receivedAt)
            {
                _lastAcknowledged = _lastSent;
                _lastAcknowledgedAt = receivedAt;
                _earlyAcknowledgementAt = null;
            }

            return true;
        }
    }

    public bool Acknowledge(MessageHeartbeatAck acknowledgement)
    {
        lock (_gate)
        {
            if (!CheckAlive() || acknowledgement.ProcessInstanceId != processInstanceId
                || acknowledgement.ProcessInstanceId == Guid.Empty
                || acknowledgement.Sequence <= _lastAcknowledged
                || acknowledgement.Sequence <= 0 || acknowledgement.Sequence > MaxSequence)
            {
                return false;
            }

            if (_sending != 0 && acknowledgement.Sequence == _sending)
            {
                _earlyAcknowledgementAt ??= time.GetTimestamp();
                return false;
            }

            if (acknowledgement.Sequence > _lastSent)
            {
                return false;
            }

            _lastAcknowledged = acknowledgement.Sequence;
            _lastAcknowledgedAt = time.GetTimestamp();
            return true;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
        }
    }

    private bool CheckAlive()
    {
        if (time.GetElapsedTime(_lastAcknowledgedAt) >= Timeout)
        {
            _stopped = true;
        }

        return !_stopped;
    }
}
