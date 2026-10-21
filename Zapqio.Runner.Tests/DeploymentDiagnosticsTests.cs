using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Zapqio.Runner.Deployments;

namespace Zapqio.Runner.Tests;

public sealed class DeploymentDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deployment-diagnostics-" + Guid.NewGuid().ToString("N"));

    private sealed record Entry(LogLevel Level, string Message, Exception? Exception, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : ILogger<DeploymentStore>
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();
        public TaskCompletionSource<Entry> LockWait { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Entry> LongHold { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(p => p.Key, p => p.Value) : [];
            var entry = new Entry(logLevel, formatter(state, exception), exception, properties);
            Entries.Enqueue(entry);
            if (entry.Message.StartsWith("Deployment journal lock wait:"))
            {
                LockWait.TrySetResult(entry);
            }

            if (entry.Message.StartsWith("Deployment journal lock still held:"))
            {
                LongHold.TrySetResult(entry);
            }
        }
    }

    [Fact]
    public async Task Contention_identifies_the_holder_across_store_instances_without_losing_the_write()
    {
        var logger = new RecordingLogger();
        var holder = new DeploymentStore(_root, logger);
        var reader = new DeploymentStore(_root, logger);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = Guid.NewGuid();
        var writing = Task.Run(() => holder.Edit(state =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException("Test did not release the journal operation.");
            }

            state.Latest[repository] = 7;
        }, "SlowReceiveProbe"));
        Task<DeploymentState>? reading = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            reading = Task.Run(() => reader.Read("ReportLoopProbe"));
            var entry = await logger.LockWait.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal("Read/ReportLoopProbe", entry.Properties["Operation"]);
            var owner = Assert.IsType<string>(entry.Properties["Owner"]);
            Assert.Contains("SlowReceiveProbe", owner);
            Assert.Contains($"PID={Environment.ProcessId}", owner);
            Assert.Contains("stage=ApplyingOperation", owner);
            var held = await logger.LongHold.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(LogLevel.Warning, held.Level);
            Assert.Equal("SlowReceiveProbe", held.Properties["Operation"]);
            Assert.Equal("ApplyingOperation", held.Properties["Stage"]);
            Assert.True(Assert.IsType<long>(held.Properties["HeldMs"]) >= 4500);
        }
        finally
        {
            release.Set();
            await writing;
            if (reading is not null)
            {
                await reading;
            }
        }

        var state = await reading!;
        Assert.Equal(7, state.Latest[repository]);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning &&
            e.Message.StartsWith("Deployment journal operation finished:") &&
            Equals(e.Properties["Operation"], "SlowReceiveProbe"));
    }

    [Fact]
    public async Task Timeout_keeps_the_original_failure_and_reports_an_unknown_holder_honestly()
    {
        var logger = new RecordingLogger();
        var store = new DeploymentStore(_root, logger);
        store.Read();
        IOException error;
        using (var held = new FileStream(Path.Combine(_root, "state.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            error = await Assert.ThrowsAsync<IOException>(() => Task.Run(() => store.Read("TimeoutProbe")));
        }

        var entry = Assert.Single(logger.Entries, e => ReferenceEquals(e.Exception, error));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Read/TimeoutProbe", entry.Properties["Operation"]);
        Assert.Equal("WaitingForLock", entry.Properties["Stage"]);
        Assert.Contains("unknown", Assert.IsType<string>(entry.Properties["Owner"]));
        Assert.True(Assert.IsType<long>(entry.Properties["ElapsedMs"]) >= 9000);
        Assert.Empty(store.Read().Packages);
    }

    [Fact]
    public void Invalid_journal_reports_its_stage_and_releases_the_lock()
    {
        var logger = new RecordingLogger();
        var store = new DeploymentStore(_root, logger);
        Directory.CreateDirectory(_root);
        var journal = Path.Combine(_root, "state.json");
        File.WriteAllText(journal, "{invalid");
        var error = Assert.Throws<JsonException>(() => store.Read("StartupProbe"));
        var entry = Assert.Single(logger.Entries, e => ReferenceEquals(e.Exception, error));
        Assert.Equal("DeserializingJournal", entry.Properties["Stage"]);
        Assert.Equal("Read/StartupProbe", entry.Properties["Operation"]);
        File.WriteAllText(journal, "{}");
        Assert.Empty(store.Read().Packages);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
