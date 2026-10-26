using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;
using Zapqio.Deployments;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner.Deployments;

public sealed class DeploymentCoordinator(DeploymentStore store, MethodsProvider methods, WSClient client,
    AppSettings settings, IHostApplicationLifetime lifetime, ILogger<DeploymentCoordinator> logger,
    IDeploymentRestart? restart = null, string? modulesRoot = null,
    ILogger<DeploymentInstaller>? installerLogger = null) : BackgroundService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<DeploymentNotice> _downloads = Channel.CreateBounded<DeploymentNotice>(new BoundedChannelOptions(200)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private volatile bool _online;
    private volatile bool _restarting;
    private Guid? _reportedBatch;
    private string? _reportedPhase;
    public Task Ready => _ready.Task;
    // Shutdown must not wait for the journal lock. Only a successfully launched
    // restart helper permits skipping the normal job drain.
    public bool RestartRequested => _restarting;
    public void Connected() => _online = true;
    public void Disconnected() => _online = false;

    public void Handle(Message message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        switch (message.Type)
        {
            case MessageType.Deployment:
                var notice = JsonSerializer.Deserialize<DeploymentNotice>(message.Data, DeploymentJson.Options);
                if (notice is not null && store.Observe(notice))
                {
                    _downloads.Writer.TryWrite(notice);
                }

                break;
            case MessageType.DeploymentApprovalResult:
                var approval = JsonSerializer.Deserialize<DeploymentApprovalResult>(message.Data, DeploymentJson.Options);
                if (approval is not null)
                {
                    var accepted = store.ApprovalReceived(approval);
                    logger.LogInformation("Deployment approval reply: batch={BatchId}, acceptedByWeb={AcceptedByWeb}, acceptedLocally={AcceptedLocally}",
                        approval.BatchId, approval.Accepted, accepted);
                }

                break;
            case MessageType.DeploymentStatusAck:
                var ack = JsonSerializer.Deserialize<DeploymentStatusAck>(message.Data, DeploymentJson.Options);
                if (ack is not null)
                {
                    store.Acknowledge(ack);
                }

                break;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Windows service startup must not wait for module constructors or publish, including on .NET 8.
        var phase = "Startup";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                methods.Initialize();
                _ready.TrySetResult();
                return;
            }

            logger.LogInformation("Deployment coordinator starting; journalRoot={JournalRoot}", store.Root);
            var installer = new DeploymentInstaller(store, modulesRoot ?? Path.Combine(AppContext.BaseDirectory, "Modules"),
                new DotnetDeploymentBuild(), methods, installerLogger);
            if (await installer.StartAsync(stoppingToken))
            {
                Restart("Module loading failed; rollback requires a fresh process");
                return;
            }

            _ready.TrySetResult();
            phase = "Workers";
            logger.LogInformation("Deployment initialization completed; runner ready, starting download and report workers");
            using var loops = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var download = DownloadLoop(loops.Token);
            var report = ReportLoop(loops.Token);

            // Neither worker may continue alone. Observe both after cancelling the sibling,
            // so a worker failure reaches the host instead of waiting forever in WhenAll.
            var completed = await Task.WhenAny(download, report);
            phase = completed == download ? "DownloadLoop" : "ReportLoop";
            if (completed.IsFaulted)
            {
                logger.LogError(completed.Exception?.GetBaseException(),
                    "Deployment worker {Worker} failed; cancelling the other worker; online={Online}, restartRequested={RestartRequested}",
                    phase, _online, _restarting);
            }

            await loops.CancelAsync();
            try
            {
                await Task.WhenAll(download, report);
            }
            catch (OperationCanceledException) when (_restarting && !stoppingToken.IsCancellationRequested)
            {
                // ReportLoop deliberately finishes after requesting a service restart.
            }

            if (!stoppingToken.IsCancellationRequested && !_restarting)
            {
                throw new InvalidOperationException("A deployment worker stopped unexpectedly.");
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
            if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Deployment coordinator cancelled during host shutdown; phase={Phase}", phase);
            }
            else
            {
                logger.LogCritical(ex,
                    "Deployment coordinator failed; exception propagates to host; phase={Phase}, online={Online}, restartRequested={RestartRequested}",
                    phase, _online, _restarting);
            }

            throw;
        }
        finally
        {
            logger.LogInformation("Deployment coordinator finished; phase={Phase}, hostCancellation={HostCancellation}, restartRequested={RestartRequested}",
                phase, stoppingToken.IsCancellationRequested, _restarting);
        }
    }

    private void Restart(string reason)
    {
        logger.LogWarning("Deployment requests runner restart: {RestartReason}", reason);
        if (restart is null)
        {
            WindowsDeploymentCli.StartRestartHelper(logger);
        }
        else
        {
            restart.Start();
        }

        _restarting = true;
        logger.LogWarning("Deployment restart helper launched; requesting host shutdown");
        lifetime.StopApplication();
    }

    private async Task ReportLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                store.Cleanup(ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Deployment cleanup failed: {Reason}", ex.Message);
            }

            if (_online && client.Connected())
            {
                DeploymentState state;
                try
                {
                    state = store.Read(cancellationToken: ct);
                }
                catch (DeploymentJournalBusyException ex)
                {
                    logger.LogWarning(ex, "Deployment reporting deferred: journal is busy; retrying in 2 seconds");
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                if (state.Batch?.Approval.BatchId != _reportedBatch || state.Batch?.Phase != _reportedPhase)
                {
                    _reportedBatch = state.Batch?.Approval.BatchId;
                    _reportedPhase = state.Batch?.Phase;
                    logger.LogInformation("Deployment batch state: batch={BatchId}, phase={Phase}, packages={PackageCount}, pendingReports={ReportCount}",
                        _reportedBatch, _reportedPhase, state.Batch?.Approval.Packages.Count ?? 0, state.Reports.Count);
                }

                foreach (var report in state.Reports)
                {
                    client.SendDeployment(MessageType.DeploymentStatus, report);
                }
                if (state.Batch is { Phase: "Requested" } request)
                {
                    client.SendDeployment(MessageType.DeploymentApproval, request.Approval);
                }
                else if (state.Batch is { Phase: "Approved" } && !_restarting)
                {
                    Restart($"Batch {state.Batch.Approval.BatchId} approved by Web");
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task DownloadLoop(CancellationToken ct)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(3) };
        await foreach (var notice in _downloads.Reader.ReadAllAsync(ct))
        {
            try
            {
                if (!store.Observe(notice))
                {
                    continue;
                }

                var elapsed = Stopwatch.StartNew();
                logger.LogInformation("Deployment download starting: deployment={DeploymentId}, repository={RepositoryId}, sequence={Sequence}, bytes={BundleBytes}",
                    notice.DeploymentId, notice.RepositoryId, notice.Sequence, notice.BundleBytes);
                var bytes = await DownloadAsync(http, settings, notice, ct);
                logger.LogInformation("Deployment downloaded; validating and saving: deployment={DeploymentId}, bytes={BundleBytes}, downloadMs={DownloadMs}",
                    notice.DeploymentId, bytes.Length, elapsed.ElapsedMilliseconds);
                var received = store.Receive(notice, bytes);
                logger.LogInformation("Deployment receive finished: deployment={DeploymentId}, awaitingApproval={AwaitingApproval}, totalMs={ElapsedMs}",
                    notice.DeploymentId, received, elapsed.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or InvalidOperationException)
            {
                // A transient network failure leaves Pending; periodic notices retry it.
                // Malformed or damaged contents are explicitly rejected.
                if (ex is InvalidDataException)
                {
                    store.Reject(notice.DeploymentId, ex.Message);
                }

                logger.LogWarning(ex, "Package {DeploymentId} could not be downloaded: {Reason}", notice.DeploymentId, ex.Message);
            }
        }
    }

    public static async Task<byte[]> DownloadAsync(HttpClient http, AppSettings settings, DeploymentNotice notice, CancellationToken ct)
    {
        var baseUri = new Uri(settings.Url.TrimEnd('/') + "/");
        var uriBuilder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme switch { "wss" => "https", "ws" => "http", _ => baseUri.Scheme } };
        if (baseUri.IsDefaultPort)
        {
            uriBuilder.Port = -1;
        }

        var expected = $"runner/deployments/{notice.DeploymentId}/bundle";
        if (notice.BundleUrl != expected || !DeploymentBundle.IsHash(notice.BundleSha256) ||
            notice.MaxBytes < 1024 || notice.MaxBytes > long.MaxValue - DeploymentBundle.MetadataLimit || notice.MaxFiles < 1 ||
            notice.BundleBytes < 1 || notice.BundleBytes > notice.MaxBytes + DeploymentBundle.MetadataLimit)
        {
            throw new InvalidDataException("Invalid deployment download notice.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uriBuilder.Uri, expected));
        request.Headers.Add("X-Zapqio-Token", settings.Token);
        request.Headers.Add("X-Zapqio-Name", settings.Name);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } size && size != notice.BundleBytes)
        {
            throw new InvalidDataException("Bundle response has an unexpected size.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > notice.BundleBytes)
            {
                throw new InvalidDataException("Bundle response exceeds the announced size.");
            }

            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
