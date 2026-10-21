using System.Collections.Concurrent;
using System.Diagnostics;

namespace Zapqio.Runner.Deployments;

// Diagnostic state only: the existing FileShare.None handle remains the actual lock.
internal sealed class DeploymentJournalOperation(string path, string operation, ILogger logger) : IDisposable
{
    private static readonly ConcurrentDictionary<string, DeploymentJournalOperation> Owners =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static long _nextId;
    private readonly long _id = Interlocked.Increment(ref _nextId);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Stopwatch _held = new();
    private string _stage = "WaitingForLock";
    private Timer? _heartbeat;
    private bool _waitReported;
    private int _ownsLock;

    public string Stage
    {
        get => Volatile.Read(ref _stage);
        set => Volatile.Write(ref _stage, value);
    }

    public static void SetStage(string path, string stage)
    {
        if (Owners.TryGetValue(path, out var owner))
        {
            owner.Stage = stage;
        }
    }

    private string OwnerDescription()
    {
        if (Owners.TryGetValue(path, out var owner))
        {
            return $"same process PID={Environment.ProcessId}, operationId={owner._id}, " +
                $"operation={owner.Operation}, thread={owner._threadId}, stage={owner.Stage}, heldMs={owner._held.ElapsedMilliseconds}";
        }

        return "unknown (another process, an uninstrumented handle, or a lock handover)";
    }

    private string Operation => operation;

    public void Waiting()
    {
        if (_waitReported || _elapsed.ElapsedMilliseconds < 1000)
        {
            return;
        }

        _waitReported = true;
        logger.LogWarning("Deployment journal lock wait: {LockPath}; operation={Operation}, operationId={OperationId}, " +
            "PID={ProcessId}, thread={ThreadId}, waitedMs={WaitedMs}, owner={Owner}",
            path, operation, _id, Environment.ProcessId, _threadId, _elapsed.ElapsedMilliseconds, OwnerDescription());
    }

    public void Acquired()
    {
        _held.Start();
        Stage = "ReadingJournal";
        Owners[path] = this;
        Volatile.Write(ref _ownsLock, 1);
        // No extra files or disk writes are introduced while acquiring the journal lock.
        if (logger.IsEnabled(LogLevel.Warning))
        {
            _heartbeat = new Timer(_ => ReportLongHold(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        logger.Log(_waitReported ? LogLevel.Warning : LogLevel.Debug,
            "Deployment journal lock acquired: {LockPath}; operation={Operation}, operationId={OperationId}, " +
            "PID={ProcessId}, thread={ThreadId}, waitedMs={WaitedMs}",
            path, operation, _id, Environment.ProcessId, _threadId, _elapsed.ElapsedMilliseconds);
    }

    private void ReportLongHold()
    {
        if (Volatile.Read(ref _ownsLock) == 0)
        {
            return;
        }

        try
        {
            logger.LogWarning("Deployment journal lock still held: {LockPath}; operation={Operation}, operationId={OperationId}, " +
                "PID={ProcessId}, thread={ThreadId}, stage={Stage}, heldMs={HeldMs}",
                path, operation, _id, Environment.ProcessId, _threadId, Stage, _held.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            // A diagnostic timer must never terminate the process if a logging provider fails.
        }
    }

    public void Failed(Exception exception)
    {
        logger.LogError(exception,
            "Deployment journal operation failed: {LockPath}; operation={Operation}, operationId={OperationId}, " +
            "PID={ProcessId}, thread={ThreadId}, stage={Stage}, elapsedMs={ElapsedMs}, heldMs={HeldMs}, owner={Owner}",
            path, operation, _id, Environment.ProcessId, _threadId, Stage,
            _elapsed.ElapsedMilliseconds, _held.ElapsedMilliseconds, OwnerDescription());
    }

    public void Release()
    {
        if (Interlocked.Exchange(ref _ownsLock, 0) == 0)
        {
            return;
        }

        _heartbeat?.Dispose();
        Owners.TryRemove(path, out _);
        _held.Stop();
    }

    public void Dispose()
    {
        Release();
        logger.Log(_held.ElapsedMilliseconds >= 1000 ? LogLevel.Warning : LogLevel.Debug,
            "Deployment journal operation finished: {LockPath}; operation={Operation}, operationId={OperationId}, " +
            "PID={ProcessId}, thread={ThreadId}, stage={Stage}, elapsedMs={ElapsedMs}, heldMs={HeldMs}",
            path, operation, _id, Environment.ProcessId, _threadId, Stage,
            _elapsed.ElapsedMilliseconds, _held.ElapsedMilliseconds);
    }
}
