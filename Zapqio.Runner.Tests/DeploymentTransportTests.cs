using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Deployments;
using Zapqio.Runner.Background;
using Zapqio.Runner.Deployments;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Tests;

public sealed class WindowsDeploymentFactAttribute : FactAttribute
{
    public WindowsDeploymentFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "SR3 deployment runtime currently supports Windows.";
        }
    }
}

public sealed class DeploymentTransportTests
{
    private sealed class Restart : IDeploymentRestart
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start() => Called.TrySetResult();
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

    private sealed class FailingWarningLogger : ILogger<DeploymentCoordinator>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            throw new ApplicationException("Unexpected download worker failure.");
        }
    }

    [WindowsDeploymentFact]
    public async Task Unexpected_failure_of_either_worker_reaches_the_background_service()
    {
        foreach (var failDownload in new[] { true, false })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var root = Path.Combine(Path.GetTempPath(), "sr3-worker-failure-" + Guid.NewGuid().ToString("N"));
            var store = new DeploymentStore(Path.Combine(root, "Deployments"));
            var settings = new AppSettings { Name = "test", Token = "test", Url = "ws://localhost" };
            var outbox = new Outbox(100);
            var modules = Directory.CreateDirectory(Path.Combine(root, "Modules"));
            using var methods = new MethodsProvider(NullLogger<MethodsProvider>.Instance,
                new JobLogWriter(outbox, settings, NullLoggerFactory.Instance), modules,
                Directory.CreateDirectory(Path.Combine(root, "Cache")), deferred: true);
            await using var client = new WSClient(settings, NullLogger<WSClient>.Instance, methods,
                outbox, new RunnerProcessState(), new PendingJobReturns());
            ILogger<DeploymentCoordinator> logger = failDownload
                ? new FailingWarningLogger() : NullLogger<DeploymentCoordinator>.Instance;
            using var coordinator = new DeploymentCoordinator(store, methods, client, settings,
                new Lifetime(), logger, new Restart(), modules.FullName);
            try
            {
                await coordinator.StartAsync(deadline.Token);
                await coordinator.Ready.WaitAsync(deadline.Token);
                if (failDownload)
                {
                    var invalid = new DeploymentNotice
                    {
                        DeploymentId = Guid.NewGuid(), RepositoryId = Guid.NewGuid(), Sequence = 1,
                        BundleUrl = "invalid"
                    };
                    coordinator.Handle(new Message
                    {
                        Type = MessageType.Deployment,
                        Data = JsonSerializer.Serialize(invalid, DeploymentJson.Options)
                    });
                    var error = await Assert.ThrowsAsync<ApplicationException>(async () =>
                        await coordinator.ExecuteTask!.WaitAsync(deadline.Token));
                    Assert.Equal("Unexpected download worker failure.", error.Message);
                }
                else
                {
                    // A valid journal with an unsupported version makes ReportLoop fail
                    // while DownloadLoop is waiting for its next package.
                    store.Edit(state => state.Version = 999);
                    await Assert.ThrowsAsync<InvalidDataException>(async () =>
                        await coordinator.ExecuteTask!.WaitAsync(deadline.Token));
                }
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await coordinator.StopAsync(stop.Token);
                // root is a unique directory created by this test under the system temp path.
                Directory.Delete(root, true);
            }
        }
    }

    [WindowsDeploymentFact]
    public async Task Http_delivery_reconnect_version_replay_and_online_approval_use_the_real_transport()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var root = Path.Combine(Path.GetTempPath(), "sr3-transport-" + Guid.NewGuid().ToString("N"));
        var store = new DeploymentStore(Path.Combine(root, "Deployments"));
        var manifest = new DeploymentManifest
        {
            DeploymentId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            SnapshotId = Guid.NewGuid(),
            Sequence = 1,
            PackageName = "sample",
            Commit = new string('a', 40),
            ContentKind = "DotnetSource"
        };
        var bytes = DeploymentBundle.Create(manifest, DeploymentBundle.Zip(new Dictionary<string, byte[]>
        { ["sample.csproj"] = "<Project/>"u8.ToArray() }), 100000, 20);
        var notice = new DeploymentNotice
        {
            DeploymentId = manifest.DeploymentId,
            RepositoryId = manifest.RepositoryId,
            Sequence = 1,
            BundleUrl = $"runner/deployments/{manifest.DeploymentId}/bundle",
            BundleSha256 = DeploymentBundle.Hash(bytes),
            BundleBytes = bytes.Length,
            MaxBytes = 100000,
            MaxFiles = 20
        };
        var damaged = notice with
        {
            DeploymentId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            BundleSha256 = new string('0', 64)
        };
        damaged = damaged with { BundleUrl = $"runner/deployments/{damaged.DeploymentId}/bundle" };
        var received = Channel.CreateUnbounded<(Message Message, WebSocket Socket)>();
        var webBuilder = WebApplication.CreateBuilder();
        webBuilder.Logging.ClearProviders();
        webBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var web = webBuilder.Build();
        web.UseWebSockets();
        var downloads = 0;
        web.MapGet("/prefix/" + notice.BundleUrl, async context =>
        {
            Assert.Equal("runner-secret", context.Request.Headers["X-Zapqio-Token"].ToString());
            Assert.Equal("machine-id", context.Request.Headers["X-Zapqio-Name"].ToString());
            Interlocked.Increment(ref downloads);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, deadline.Token);
        });
        web.MapGet("/prefix/" + damaged.BundleUrl, async context =>
        {
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, deadline.Token);
        });
        web.Map("/prefix/ws-runner", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                var buffer = new byte[8192];
                while (!deadline.IsCancellationRequested && socket.State == WebSocketState.Open)
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
                    } while (!frame.EndOfMessage);
                    await received.Writer.WriteAsync((JsonSerializer.Deserialize<Message>(data.ToArray(), JsonDefaults.Options)!, socket), deadline.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        });
        await web.StartAsync(deadline.Token);
        var settings = new AppSettings
        {
            Name = "machine-id",
            Token = "runner-secret",
            Url = web.Urls.Single().Replace("http://", "ws://") + "/prefix",
            StopTimeoutSeconds = 0
        };
        settings.Normalize();
        var outbox = new Outbox(1000);
        var pending = new PendingJobReturns();
        var process = new RunnerProcessState();
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules"));
        using var methods = new MethodsProvider(NullLogger<MethodsProvider>.Instance,
            new JobLogWriter(outbox, settings, NullLoggerFactory.Instance), modules,
            Directory.CreateDirectory(Path.Combine(root, "Cache")), deferred: true);
        await using var client = new WSClient(settings, NullLogger<WSClient>.Instance, methods, outbox, process, pending);
        using var sender = new OutboundSender(outbox, client, NullLogger<OutboundSender>.Instance);
        var restart = new Restart();
        using var deployments = new DeploymentCoordinator(store, methods, client, settings, new Lifetime(),
            NullLogger<DeploymentCoordinator>.Instance, restart, modules.FullName);
        var scheduler = new JobScheduler(1, _ => Task.CompletedTask, _ => Task.CompletedTask, client.SendQueryOnJob,
            NullLogger<JobScheduler>.Instance, process);
        using var binder = new RequestBindBackground(client, NullLogger<RequestBindBackground>.Instance, pending, scheduler,
            outbox, sender, settings, deployments);
        await sender.StartAsync(deadline.Token);
        await deployments.StartAsync(deadline.Token);
        await binder.StartAsync(deadline.Token);
        async Task<(T Data, WebSocket Socket)> Read<T>(MessageType type)
        {
            while (true)
            {
                var item = await received.Reader.ReadAsync(deadline.Token);
                if (item.Message.Type == type)
                {
                    return (JsonSerializer.Deserialize<T>(item.Message.Data, JsonDefaults.Options)!, item.Socket);
                }
            }
        }
        Task Send(WebSocket socket, MessageType type, object data) => socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(
            new Message { Type = type, Data = JsonSerializer.Serialize(data, DeploymentJson.Options) }, JsonDefaults.Options)),
            WebSocketMessageType.Text, true, deadline.Token);
        try
        {
            var first = await Read<MessageInfo>(MessageType.Info);
            var version = new ExecutionVersion(Guid.NewGuid(), Guid.NewGuid(), new string('b', 40)) { DeploymentId = Guid.NewGuid() };
            var result = new MessageJobReturn
            {
                Id = Guid.NewGuid(),
                AttemptId = Guid.NewGuid(),
                Status = MessageResponseStatus.OK,
                Data = "old-version-result",
                ExecutionVersion = version
            };
            var sent = await client.SendJobReturn(result.Id, result.AttemptId, result.Status, result.Data, version);
            pending.MarkSent(result, sent!.Value);
            await Read<MessageJobReturn>(MessageType.JobReturn);
            await Send(first.Socket, MessageType.Deployment, damaged);
            var rejected = (await Read<DeploymentReport>(MessageType.DeploymentStatus)).Data;
            var rejectedItem = Assert.Single(rejected.Items);
            Assert.Equal(damaged.DeploymentId, rejectedItem.DeploymentId);
            Assert.Equal("Rejected", rejectedItem.Status);
            Assert.False(string.IsNullOrWhiteSpace(rejectedItem.Reason));
            await Send(first.Socket, MessageType.DeploymentStatusAck,
                new DeploymentStatusAck([new(rejectedItem.DeploymentId, rejectedItem.Revision)]));
            await Send(first.Socket, MessageType.Deployment, notice);
            DeploymentReport report;
            do
            {
                report = (await Read<DeploymentReport>(MessageType.DeploymentStatus)).Data;
            } while (report.Items.All(item => item.DeploymentId != notice.DeploymentId));
            Assert.Equal("AwaitingApproval", Assert.Single(report.Items).Status);
            await Send(first.Socket, MessageType.DeploymentStatusAck, new DeploymentStatusAck(report.Items.Select(i => new DeploymentReceipt(i.DeploymentId, i.Revision)).ToList()));
            Assert.Equal(1, downloads);
            Assert.Empty(Directory.GetFiles(modules.FullName));
            Assert.Equal(1, pending.Count);
            first.Socket.Abort();
            var second = await Read<MessageInfo>(MessageType.Info);
            var replay = (await Read<MessageJobReturn>(MessageType.JobReturn)).Data;
            Assert.Equal(result.AttemptId, replay.AttemptId);
            Assert.Equal(version, replay.ExecutionVersion);
            var approval = store.RequestApproval(null);
            var requested = (await Read<DeploymentApproval>(MessageType.DeploymentApproval)).Data;
            Assert.Equal(approval.BatchId, requested.BatchId);
            Assert.Equal(notice.BundleSha256, requested.Packages.Single().BundleSha256);
            Assert.False(restart.Called.Task.IsCompleted);
            Assert.False(store.RestartRequested);
            await Send(second.Socket, MessageType.DeploymentApprovalResult, new DeploymentApprovalResult(approval.BatchId, true, null));
            await restart.Called.Task.WaitAsync(deadline.Token);
            Assert.True(store.RestartRequested);
            Assert.Equal("Approved", store.Read().Batch!.Phase);
            await deployments.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await binder.StopAsync(stop.Token);
            await deployments.StopAsync(stop.Token);
            await sender.StopAsync(stop.Token);
            await web.StopAsync(stop.Token);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
