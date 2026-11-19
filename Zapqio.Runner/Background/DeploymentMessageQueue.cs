using System.Text;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Background;

/// <summary>
/// Ordered session buffer. Bursts spill to temporary files so a busy deployment journal does not
/// block heartbeat acknowledgement reception or require unbounded managed memory. This is not a durable inbox.
/// </summary>
internal sealed class DeploymentMessageQueue(string? tempDirectory = null) : IDisposable
{
    private const int MemoryLimit = 4 * 1024 * 1024;
    private const int SegmentLimit = 8 * 1024 * 1024;
    private const long PendingLimit = 256L * 1024 * 1024;
    private readonly string _tempDirectory = tempDirectory ?? Path.GetTempPath();
    private readonly object _gate = new();
    private readonly Queue<Message> _memory = new();
    private readonly Queue<Segment> _segments = new();
    private readonly SemaphoreSlim _available = new(0);
    private Segment? _tail;
    private long _memoryBytes;
    private long _pendingBytes;
    private bool _completed;

    public void Write(Message message)
    {
        lock (_gate)
        {
            if (_completed)
            {
                throw new InvalidOperationException("The deployment message queue is complete.");
            }

            var size = SizeOf(message);
            if (_pendingBytes + size > PendingLimit)
            {
                throw new InvalidOperationException("Deployment pending messages exceeded the 256 MiB session buffer limit.");
            }

            if (_segments.Count == 0 && _memory.Count < 64 && _memoryBytes + size <= MemoryLimit)
            {
                _memory.Enqueue(message);
                _memoryBytes += size;
            }
            else
            {
                if (_tail == null || _tail.Length >= SegmentLimit)
                {
                    _tail = new Segment(_tempDirectory);
                    _segments.Enqueue(_tail);
                }

                _tail.Write(message);
            }

            _pendingBytes += size;
            _available.Release();
        }
    }

    public async ValueTask<Message?> ReadAsync(CancellationToken token)
    {
        await _available.WaitAsync(token);
        lock (_gate)
        {
            if (_memory.TryDequeue(out var message))
            {
                _memoryBytes -= SizeOf(message);
                _pendingBytes -= SizeOf(message);
                return message;
            }

            if (_segments.TryPeek(out var segment))
            {
                message = segment.Read();
                _pendingBytes -= SizeOf(message);
                if (segment.IsEmpty)
                {
                    _segments.Dequeue();
                    if (_tail == segment)
                    {
                        _tail = null;
                    }

                    segment.Dispose();
                }

                return message;
            }

            return null;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                _available.Release();
            }
        }
    }

    public void Dispose()
    {
        foreach (var segment in _segments)
        {
            segment.Dispose();
        }

        _available.Dispose();
    }

    private static long SizeOf(Message message) => 64L + (long)(message.Data?.Length ?? 0) * sizeof(char);

    private sealed class Segment : IDisposable
    {
        private readonly FileStream _file;
        private long _readPosition;

        public Segment(string tempDirectory)
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.DeleteOnClose
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            _file = new FileStream(Path.Combine(tempDirectory, $"zapq-deployments-{Guid.NewGuid():N}.tmp"), options);
        }

        public long Length => _file.Length;
        public bool IsEmpty => _readPosition == Length;

        public void Write(Message message)
        {
            _file.Position = _file.Length;
            using var writer = new BinaryWriter(_file, Encoding.UTF8, leaveOpen: true);
            writer.Write((int)message.Type);
            writer.Write(message.Data != null);
            if (message.Data != null)
            {
                writer.Write(message.Data);
            }
        }

        public Message Read()
        {
            _file.Position = _readPosition;
            using var reader = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true);
            var message = new Message
            {
                Type = (MessageType)reader.ReadInt32(),
                Data = reader.ReadBoolean() ? reader.ReadString() : null!
            };
            _readPosition = _file.Position;
            return message;
        }

        public void Dispose() => _file.Dispose();
    }
}
