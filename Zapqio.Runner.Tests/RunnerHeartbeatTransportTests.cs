using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Zapqio.Deployments;
using Zapqio.Runner.Background;
using Zapqio.Runner.Deployments;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public sealed class RunnerHeartbeatTransportTests
{
    private sealed class Peer(WebSocket socket)
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        public async Task SendAsync(MessageType type, object payload, CancellationToken token)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(Outbox.Frame(type, payload), JsonDefaults.Options);
            await _sendGate.WaitAsync(token);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            finally
            {
                _sendGate.Release();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_silent_job_keeps_pulsing_and_survives_one_way_transport_failure(bool discardPulses)
    {
        await ExerciseTransportAsync(discardPulses, blockDeploymentJournal: false);
    }

    [WindowsDeploymentFact]
    public async Task A_blocked_deployment_journal_does_not_block_heartbeat_acknowledgements()
    {
        await ExerciseTransportAsync(discardPulses: false, blockDeploymentJournal: true);
    }

    [WindowsDeploymentFact]
    public async Task Deployment_burst_while_the_journal_is_locked_keeps_heartbeat_and_all_notices()
    {
        await ExerciseTransportAsync(discardPulses: false, blockDeploymentJournal: true, deploymentBurstSize: 128);
    }

    private static async Task ExerciseTransportAsync(bool discardPulses, bool blockDeploymentJournal, int deploymentBurstSize = 1)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var clock = new HeartbeatClock();
        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "heartbeat-transport-" + Guid.NewGuid().ToString("N")));
        var infos = Channel.CreateUnbounded<(MessageInfo Info, Peer Peer, string Version, string Process)>();
        var pulses = Channel.CreateUnbounded<MessageHeartbeat>();
        var receivedPulses = Channel.CreateUnbounded<MessageHeartbeat>();
        var results = Channel.CreateUnbounded<MessageJobReturn>();
        var failingPeer = (Peer?)null;
        var acknowledge = true;
        var webBuilder = WebApplication.CreateBuilder();
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var web = webBuilder.Build();
        web.UseWebSockets();
        web.Map("/ws-runner", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var peer = new Peer(socket);
            try
            {
                var buffer = new byte[8192];
                while (socket.State == WebSocketState.Open && !deadline.IsCancellationRequested)
                {
                    using var data = new MemoryStream();
                    WebSocketReceiveResult frame;
                    do
                    {
                        frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                        if (frame.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
                            return;
                        }

                        data.Write(buffer, 0, frame.Count);
                    }
                    while (!frame.EndOfMessage);

                    var message = JsonSerializer.Deserialize<Message>(data.ToArray(), JsonDefaults.Options)!;
                    switch (message.Type)
                    {
                        case MessageType.Info:
                            await infos.Writer.WriteAsync((JsonSerializer.Deserialize<MessageInfo>(message.Data, JsonDefaults.Options)!, peer,
                                context.Request.Headers[ProtocolVersion.Header].ToString(),
                                context.Request.Headers[MessageInfo.ProcessInstanceHeader].ToString()), deadline.Token);
                            break;
                        case MessageType.Heartbeat:
                            var pulse = JsonSerializer.Deserialize<MessageHeartbeat>(message.Data, JsonDefaults.Options)!;
                            await receivedPulses.Writer.WriteAsync(pulse, deadline.Token);
                            if (ReferenceEquals(peer, Volatile.Read(ref failingPeer)) && discardPulses)
                            {
                                // The transport is open, but application traffic never reaches Web.
                                break;
                            }

                            if (Volatile.Read(ref acknowledge))
                            {
                                await peer.SendAsync(MessageType.HeartbeatAck,
                                    new MessageHeartbeatAck { ProcessInstanceId = pulse.ProcessInstanceId, Sequence = pulse.Sequence }, deadline.Token);
                            }

                            await pulses.Writer.WriteAsync(pulse, deadline.Token);
                            break;
                        case MessageType.JobReturn:
                            await results.Writer.WriteAsync(JsonSerializer.Deserialize<MessageJobReturn>(message.Data, JsonDefaults.Options)!, deadline.Token);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Expected when the runner aborts an expired session.
            }
        });
        await web.StartAsync(deadline.Token);
        var settings = new AppSettings { Name = "heartbeat-test", Token = "test", Url = web.Urls.Single().Replace("http://", "ws://"), MaxConcurrency = 1, StopTimeoutSeconds = 0 };
        var process = new RunnerProcessState();
        var pending = new PendingJobReturns();
        var outbox = new Outbox(1000);
        using var methods = new MethodsProvider(NullLogger<MethodsProvider>.Instance, new JobLogWriter(outbox, settings, NullLoggerFactory.Instance),
            Directory.CreateDirectory(Path.Combine(tempRoot, "Modules")), Directory.CreateDirectory(Path.Combine(tempRoot, "Cache")));
        await using var client = new WSClient(settings, NullLogger<WSClient>.Instance, methods, outbox, process, pending, clock);
        using var sender = new OutboundSender(outbox, client, NullLogger<OutboundSender>.Instance);
        var journalLogger = new JournalWaitLogger();
        var journal = new DeploymentStore(Path.Combine(tempRoot, "Deployments"), journalLogger);
        using var deployments = blockDeploymentJournal
            ? new DeploymentCoordinator(journal, methods, client, settings, new Lifetime(), NullLogger<DeploymentCoordinator>.Instance,
                modulesRoot: Path.Combine(tempRoot, "Modules")) : null;
        using var releaseJournal = new ManualResetEventSlim();
        var journalHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdingJournal = Task.CompletedTask;
        var deploymentIds = new List<Guid>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var scheduler = new JobScheduler(1, async job =>
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await finish.Task.WaitAsync(deadline.Token);
            var result = new MessageJobReturn { Id = job.Id, AttemptId = job.AttemptId, Status = MessageResponseStatus.OK, Data = "one-execution" };
            var sequence = await client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data);
            if (sequence is { } sent)
            {
                pending.MarkSent(result, sent);
            }
            else
            {
                pending.MarkFailed(result);
            }
        }, job => client.SendJobAccepted(job.Id, job.AttemptId), client.SendQueryOnJob, NullLogger<JobScheduler>.Instance, process);
        using var binder = new RequestBindBackground(client, NullLogger<RequestBindBackground>.Instance, pending, scheduler, outbox, sender, settings, deployments);
        using var schedulerStop = new CancellationTokenSource();
        var scheduling = scheduler.RunAsync(schedulerStop.Token);
        await sender.StartAsync(deadline.Token);
        if (deployments is not null)
        {
            await deployments.StartAsync(deadline.Token);
        }

        await binder.StartAsync(deadline.Token);
        try
        {
            var first = await infos.Reader.ReadAsync(deadline.Token);
            Assert.Equal("4", first.Version);
            Assert.Equal(process.InstanceId.ToString(), first.Process);
            Assert.Empty(first.Info.ActiveAttemptIds);
            var originalConnection = client.ConnectionId;
            var firstPulse = await pulses.Reader.ReadAsync(deadline.Token);
            Assert.Equal(1, firstPulse.Sequence);
            await RunnerConnectionTests.EventuallyAsync(() => client.LastHeartbeatAcknowledgedSequence == 1);
            clock.Advance(TimeSpan.FromSeconds(10));
            await pulses.Reader.ReadAsync(deadline.Token);
            await RunnerConnectionTests.EventuallyAsync(() => client.LastHeartbeatAcknowledgedSequence == 2);

            var job = new MessageJob { Id = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Name = "Silent", Data = "{}" };
            await first.Peer.SendAsync(MessageType.Job, job, deadline.Token);
            await started.Task.WaitAsync(deadline.Token);
            if (blockDeploymentJournal)
            {
                holdingJournal = Task.Run(() => journal.Edit(_ =>
                {
                    journalHeld.TrySetResult();
                    if (!releaseJournal.Wait(TimeSpan.FromSeconds(15)))
                    {
                        throw new TimeoutException("Journal test did not release its lock.");
                    }
                }, "HeartbeatTestHold"));
                await journalHeld.Task.WaitAsync(deadline.Token);
                for (var n = 0; n < deploymentBurstSize; n++)
                {
                    var deploymentId = Guid.NewGuid();
                    deploymentIds.Add(deploymentId);
                    await first.Peer.SendAsync(MessageType.Deployment, new DeploymentNotice
                    {
                        DeploymentId = deploymentId, RepositoryId = Guid.NewGuid(), Sequence = 1, Status = "Pending"
                    }, deadline.Token);
                    if (n == 0)
                    {
                        await journalLogger.ObserveBlocked.Task.WaitAsync(deadline.Token);
                    }
                }
            }

            for (var sequence = 3; sequence <= 7; sequence++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                Assert.Equal(sequence, (await pulses.Reader.ReadAsync(deadline.Token)).Sequence);
                await RunnerConnectionTests.EventuallyAsync(() => client.LastHeartbeatAcknowledgedSequence == sequence);
            }

            releaseJournal.Set();
            await holdingJournal.WaitAsync(deadline.Token);
            if (blockDeploymentJournal)
            {
                await RunnerConnectionTests.EventuallyAsync(() => journal.Read().Packages.Count == deploymentBurstSize);
                Assert.Equal(deploymentIds.Order(), journal.Read().Packages.Select(x => x.Notice.DeploymentId).Order());
            }

            Assert.Equal(1, scheduler.Running);
            Assert.Equal(0, scheduler.FreeSlots);
            Assert.Equal(originalConnection, client.ConnectionId);
            // Lose exactly one direction while leaving the WebSocket formally open.
            Volatile.Write(ref failingPeer, first.Peer);
            Volatile.Write(ref acknowledge, discardPulses);
            while (receivedPulses.Reader.TryRead(out _))
            {
            }

            for (var n = 0; n < 4; n++)
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                await receivedPulses.Reader.ReadAsync(deadline.Token);
                if (!discardPulses)
                {
                    await pulses.Reader.ReadAsync(deadline.Token);
                }
            }

            clock.Advance(TimeSpan.FromSeconds(5));
            var second = await infos.Reader.ReadAsync(deadline.Token);
            Volatile.Write(ref acknowledge, true);
            Assert.NotEqual(originalConnection, client.ConnectionId);
            Assert.Equal(process.InstanceId, second.Info.ProcessInstanceId);
            Assert.Equal(job.AttemptId, Assert.Single(second.Info.ActiveAttemptIds));
            var restartedPulse = await pulses.Reader.ReadAsync(deadline.Token);
            Assert.Equal(1, restartedPulse.Sequence);
            Assert.False(client.HandleHeartbeatAck(originalConnection, JsonSerializer.Serialize(
                new MessageHeartbeatAck { ProcessInstanceId = process.InstanceId, Sequence = 1 }, JsonDefaults.Options)));
            Assert.False(client.HandleHeartbeatAck(client.ConnectionId, "{\"sequence\":999}"));
            Assert.False(client.HandleHeartbeatAck(client.ConnectionId, JsonSerializer.Serialize(
                new MessageHeartbeatAck { ProcessInstanceId = process.InstanceId, Sequence = 999 }, JsonDefaults.Options)));

            finish.TrySetResult();
            var result = await results.Reader.ReadAsync(deadline.Token);
            Assert.Equal(job.AttemptId, result.AttemptId);
            Assert.Equal("one-execution", result.Data);
            await RunnerConnectionTests.EventuallyAsync(() => pending.Count == 1 && scheduler.Running == 0);
            clock.Advance(TimeSpan.FromSeconds(10));
            var lastPulse = await pulses.Reader.ReadAsync(deadline.Token);
            await RunnerConnectionTests.EventuallyAsync(() => client.LastHeartbeatAcknowledgedSequence == lastPulse.Sequence);
            Assert.Equal(1, pending.Count);
            Assert.Equal(1, Volatile.Read(ref executions));
        }
        finally
        {
            finish.TrySetResult();
            releaseJournal.Set();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await holdingJournal.WaitAsync(stop.Token);
            await binder.StopAsync(stop.Token);
            await schedulerStop.CancelAsync();
            await scheduling;
            if (deployments is not null)
            {
                await deployments.StopAsync(stop.Token);
            }

            await sender.StopAsync(stop.Token);
            await web.StopAsync(stop.Token);
            // Only the unique directory created by this test under the system temp root is removed.
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), tempRoot, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task V3_web_refuses_the_v4_runner_with_an_explicit_compatibility_message()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var web = builder.Build();
        web.Map("/ws-runner", context =>
        {
            Assert.Equal("4", context.Request.Headers[ProtocolVersion.Header].ToString());
            context.Response.StatusCode = 426;
            context.Response.Headers[ProtocolVersion.Header] = "3";
            return Task.CompletedTask;
        });
        await web.StartAsync();
        var settings = new AppSettings { Name = "version-test", Token = "test", Url = web.Urls.Single().Replace("http://", "ws://") };
        var outbox = new Outbox(100);
        using var methods = new MethodsProvider(NullLogger<MethodsProvider>.Instance, new JobLogWriter(outbox, settings, NullLoggerFactory.Instance));
        var logger = new Messages();
        await using var client = new WSClient(settings, logger, methods, outbox, new RunnerProcessState(), new PendingJobReturns());
        var result = await client.Connect();
        Assert.False(result.Connected);
        Assert.Equal(HttpStatusCode.UpgradeRequired, result.Status);
        Assert.Contains(logger.Lines, line => line.Contains("protocol v4") && line.Contains("Web protocol 3"));
    }

    private sealed class Messages : ILogger<WSClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication()
        {
        }
    }

    private sealed class JournalWaitLogger : ILogger<DeploymentStore>
    {
        public TaskCompletionSource ObserveBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (text.StartsWith("Deployment journal lock wait:") && text.Contains("operation=Observe,"))
            {
                ObserveBlocked.TrySetResult();
            }
        }
    }
}
