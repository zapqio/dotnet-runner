using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Deployments;

namespace Zapqio.Runner.Deployments;

public sealed class LocalDeployment
{
    public DeploymentNotice Notice { get; set; } = new();
    public DeploymentManifest? Manifest { get; set; }
    public string Status { get; set; } = "Pending";
    public long Revision { get; set; }
    public string? Reason { get; set; }
}
public sealed record InstalledModule(DeploymentManifest Manifest, string ZipSha256);
public sealed record ModuleBackup(string PackageName, bool Existed, string? Sha256);
public sealed class LocalBatch
{
    public DeploymentApproval Approval { get; set; } = new(Guid.Empty, []);
    public string Phase { get; set; } = "Requested";
    public string? Reason { get; set; }
    public List<ModuleBackup> Backups { get; set; } = [];
    public List<InstalledModule> Previous { get; set; } = [];
}
public sealed class DeploymentState
{
    public int Version { get; set; } = 1;
    public Dictionary<Guid, long> Latest { get; set; } = [];
    public List<LocalDeployment> Packages { get; set; } = [];
    public List<InstalledModule> Installed { get; set; } = [];
    public LocalBatch? Batch { get; set; }
    public List<DeploymentReport> Reports { get; set; } = [];
}

// A single atomic JSON journal is shared by the service and the administrative CLI.
// Opening the lock with FileShare.None also serializes independent CLI processes.
public sealed class DeploymentStore(string root, ILogger<DeploymentStore>? logger = null)
{
    private readonly ILogger<DeploymentStore> _logger = logger ?? NullLogger<DeploymentStore>.Instance;
    public string Root { get; } = Path.GetFullPath(root);
    private string LockPath => Path.Combine(Root, "state.lock");
    public string BundlePath(Guid id) => Path.Combine(Root, "bundles", id.ToString("N") + ".zip");
    public string WorkPath(Guid batch) => Path.Combine(Root, "work", batch.ToString("N"));

    public DeploymentState Read([CallerMemberName] string caller = "", CancellationToken cancellationToken = default) =>
        Access(s => s, save: false, "Read/" + caller, cancellationToken);
    public T Edit<T>(Func<DeploymentState, T> action, [CallerMemberName] string operation = "") => Access(action, save: true, operation);
    public void Edit(Action<DeploymentState> action, [CallerMemberName] string operation = "") => Access(action, save: true, operation);

    private void Access(Action<DeploymentState> action, bool save, string operation, CancellationToken cancellationToken = default) => Access(s =>
    {
        action(s);
        return true;
    }, save, operation, cancellationToken);

    private T Access<T>(Func<DeploymentState, T> action, bool save, string operation, CancellationToken cancellationToken = default)
    {
        using var diagnostics = new DeploymentJournalOperation(LockPath, operation, _logger);
        FileStream? held = null;
        try
        {
            DeploymentBundle.RequireRegularPath(Root);
            Directory.CreateDirectory(Root);
            DeploymentBundle.RequireRegularPath(LockPath);
            var waiting = Stopwatch.StartNew();
            while (held is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    held = new(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException ex)
                {
                    if (waiting.Elapsed >= TimeSpan.FromSeconds(10))
                    {
                        throw new DeploymentJournalBusyException(LockPath, ex);
                    }

                    diagnostics.Waiting();
                    cancellationToken.WaitHandle.WaitOne(20);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.Acquired();
            var path = Path.Combine(Root, "state.json");
            DeploymentBundle.RequireRegularPath(path);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            diagnostics.Stage = "DeserializingJournal";
            var state = bytes is not null
                ? JsonSerializer.Deserialize<DeploymentState>(bytes, DeploymentJson.Options)
                    ?? throw new InvalidDataException("Deployment journal is empty.")
                : new DeploymentState();
            if (state.Version != 1)
            {
                throw new InvalidDataException("Unsupported deployment journal version.");
            }

            _logger.LogDebug("Deployment journal read: {JournalBytes} bytes, {PackageCount} packages, {ReportCount} reports, batch={BatchId}, phase={Phase}",
                bytes?.Length ?? 0, state.Packages.Count, state.Reports.Count, state.Batch?.Approval.BatchId, state.Batch?.Phase);
            diagnostics.Stage = "ApplyingOperation";
            var result = action(state);
            if (save)
            {
                diagnostics.Stage = "SerializingJournal";
                var updated = JsonSerializer.SerializeToUtf8Bytes(state, DeploymentJson.Options);
                // Replayed notices and ACKs often leave the journal unchanged. Avoid another
                // durable flush under the lock, especially while a backup delays disk writes.
                if (bytes is null || !bytes.AsSpan().SequenceEqual(updated))
                {
                    diagnostics.Stage = $"WritingJournal ({updated.Length} bytes)";
                    DeploymentBundle.AtomicWrite(path, updated);
                }
            }

            diagnostics.Stage = "Completed";
            return result;
        }
        catch (Exception ex)
        {
            diagnostics.Failed(ex);
            throw;
        }
        finally
        {
            // Unregister before releasing the file so the next owner cannot be removed here.
            diagnostics.Release();
            held?.Dispose();
        }
    }

    private static bool Pinned(DeploymentState s, Guid id) => s.Batch is { Phase: "Requested" or "Approved" or "Preparing" or "Switching" or "Loading" } b &&
        b.Approval.Packages.Any(p => p.DeploymentId == id);

    public bool Observe(DeploymentNotice notice) => Edit(s =>
    {
        if (notice.DeploymentId == Guid.Empty || notice.RepositoryId == Guid.Empty || notice.Sequence < 1)
        {
            return false;
        }

        var latest = s.Latest.GetValueOrDefault(notice.RepositoryId);
        if (notice.Sequence < latest)
        {
            return false;
        }

        s.Latest[notice.RepositoryId] = notice.Sequence;
        foreach (var old in s.Packages.Where(p => p.Notice.RepositoryId == notice.RepositoryId && p.Notice.Sequence < notice.Sequence &&
            !Pinned(s, p.Notice.DeploymentId) && p.Status is "Pending" or "AwaitingApproval"))
        {
            SetStatus(s, old, "Superseded", "Replaced by a newer submission.");
        }
        var package = s.Packages.SingleOrDefault(p => p.Notice.DeploymentId == notice.DeploymentId);
        if (package is null)
        {
            package = new() { Notice = notice };
            s.Packages.Add(package);
        }
        else if (!Pinned(s, notice.DeploymentId))
        {
            if (package.Manifest is not null && package.Notice.BundleSha256 != notice.BundleSha256)
            {
                throw new InvalidDataException("An immutable deployment bundle changed.");
            }

            package.Notice = notice;
        }

        if (notice.Status is not "Pending" and not "AwaitingApproval")
        {
            if (!Pinned(s, notice.DeploymentId))
            {
                package.Status = notice.Status;
            }

            return false;
        }

        return package.Status == "Pending" && notice.BundleUrl is not null && !Pinned(s, notice.DeploymentId);
    });

    public bool Receive(DeploymentNotice notice, byte[] bytes)
    {
        var decoded = DeploymentBundle.Read(bytes, notice.BundleSha256!, notice.MaxBytes, notice.MaxFiles);
        var m = decoded.Manifest;
        if (m.DeploymentId != notice.DeploymentId || m.RepositoryId != notice.RepositoryId || m.Sequence != notice.Sequence || bytes.LongLength != notice.BundleBytes)
        {
            throw new InvalidDataException("Downloaded bundle identity differs from its notice.");
        }

        return Edit(s =>
        {
            var package = s.Packages.SingleOrDefault(p => p.Notice.DeploymentId == notice.DeploymentId);
            if (package is null || package.Status != "Pending" || s.Latest.GetValueOrDefault(m.RepositoryId) != m.Sequence)
            {
                return false;
            }

            DeploymentJournalOperation.SetStage(LockPath, $"SavingBundle {m.DeploymentId} ({bytes.Length} bytes)");
            DeploymentBundle.AtomicWrite(BundlePath(m.DeploymentId), bytes);
            package.Manifest = m;
            SetStatus(s, package, "AwaitingApproval", null);
            return true;
        });
    }

    public DeploymentApproval RequestApproval(IReadOnlyCollection<Guid>? selected) => Edit(s =>
    {
        if (s.Batch is { Phase: "Requested" or "Approved" or "Preparing" or "Switching" or "Loading" })
        {
            throw new InvalidOperationException("An approval or installation is already in progress.");
        }

        if (s.Batch is not null && s.Reports.Any(r => r.BatchId == s.Batch.Approval.BatchId))
        {
            throw new InvalidOperationException("The previous result has not been acknowledged by Web yet.");
        }

        var packages = s.Packages.Where(p => p.Status == "AwaitingApproval" && p.Manifest is not null &&
            s.Latest.GetValueOrDefault(p.Notice.RepositoryId) == p.Notice.Sequence &&
            (selected is null || selected.Contains(p.Notice.DeploymentId))).ToList();
        if (packages.Count == 0 || packages.Count > 200 || selected is not null && packages.Count != selected.Distinct().Count())
        {
            throw new InvalidOperationException("Select currently pending deployments, or use approve --all.");
        }

        foreach (var p in packages)
        {
            DeploymentJournalOperation.SetStage(LockPath, $"ValidatingApprovalBundle {p.Notice.DeploymentId}");
            DeploymentBundle.Read(File.ReadAllBytes(BundlePath(p.Notice.DeploymentId)), p.Notice.BundleSha256!, p.Notice.MaxBytes, p.Notice.MaxFiles);
        }
        var request = new DeploymentApproval(Guid.NewGuid(), packages.Select(p => new PackageApproval(p.Notice.DeploymentId, p.Notice.BundleSha256!)).ToList());
        s.Batch = new() { Approval = request };
        return request;
    });

    public bool ApprovalReceived(DeploymentApprovalResult reply) => Edit(s =>
    {
        if (s.Batch is not { Phase: "Requested" } b || b.Approval.BatchId != reply.BatchId)
        {
            return false;
        }

        b.Phase = reply.Accepted ? "Approved" : "Denied";
        b.Reason = reply.Reason;
        if (!reply.Accepted)
        {
            foreach (var p in s.Packages.Where(p => s.Latest.GetValueOrDefault(p.Notice.RepositoryId) > p.Notice.Sequence && p.Status == "AwaitingApproval"))
            {
                SetStatus(s, p, "Superseded", "Replaced by a newer submission.");
            }
        }

        return reply.Accepted;
    });

    public void Reject(Guid id, string reason) => Edit(s =>
    {
        var p = s.Packages.SingleOrDefault(p => p.Notice.DeploymentId == id) ?? throw new InvalidOperationException("Unknown deployment.");
        if (Pinned(s, id))
        {
            throw new InvalidOperationException("An approved or requested package cannot be rejected locally.");
        }

        if (p.Status is "Pending" or "AwaitingApproval")
        {
            SetStatus(s, p, "Rejected", reason);
        }
    });

    public void Acknowledge(DeploymentStatusAck ack) => Edit(s =>
        s.Reports.RemoveAll(r => r.Items.All(i => ack.Items.Any(a => a.DeploymentId == i.DeploymentId && a.Revision == i.Revision))));

    public static void SetStatus(DeploymentState s, LocalDeployment p, string status, string? reason)
    {
        p.Status = status;
        p.Reason = reason;
        p.Revision++;
        s.Reports.RemoveAll(r => r.BatchId is null && r.Items.Any(i => i.DeploymentId == p.Notice.DeploymentId));
        s.Reports.Add(new(null, [new(p.Notice.DeploymentId, p.Revision, status, reason)]));
    }

    public void Finish(string status, string? reason, List<InstalledModule>? installed = null) => Edit(s =>
    {
        var b = s.Batch ?? throw new InvalidOperationException("No deployment batch.");
        b.Phase = status;
        b.Reason = reason;
        if (installed is not null)
        {
            s.Installed = installed;
        }

        var items = new List<DeploymentStatusItem>();
        foreach (var selected in b.Approval.Packages)
        {
            var p = s.Packages.Single(p => p.Notice.DeploymentId == selected.DeploymentId);
            p.Status = status;
            p.Reason = reason;
            p.Revision++;
            items.Add(new(selected.DeploymentId, p.Revision, status, reason));
        }
        s.Reports.RemoveAll(r => r.BatchId == b.Approval.BatchId || r.Items.Any(i => items.Any(n => n.DeploymentId == i.DeploymentId)));
        s.Reports.Add(new(b.Approval.BatchId, items));
    });

    public void Cleanup(CancellationToken cancellationToken = default) => Access(s =>
    {
        var keep = s.Packages.Where(p => p.Status is "Pending" or "AwaitingApproval" || Pinned(s, p.Notice.DeploymentId))
            .Select(p => p.Notice.DeploymentId).ToHashSet();
        var dir = Path.GetDirectoryName(BundlePath(Guid.Empty))!;
        DeploymentBundle.RequireRegularPath(dir);
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.zip"))
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && !keep.Contains(id))
                {
                    DeploymentBundle.RequireRegularPath(file);
                    DeploymentJournalOperation.SetStage(LockPath, $"DeletingBundle {id}");
                    File.Delete(file);
                }
            }
        }

        var work = Path.Combine(Root, "work");
        DeploymentBundle.RequireRegularPath(work);
        if (!Directory.Exists(work))
        {
            return;
        }

        var active = s.Batch is { Phase: "Requested" or "Approved" or "Preparing" or "Switching" or "Loading" } b ? b.Approval.BatchId : Guid.Empty;
        foreach (var path in Directory.EnumerateDirectories(work))
        {
            if (Guid.TryParseExact(Path.GetFileName(path), "N", out var id) && id != active)
            {
                DeploymentBundle.RequireRegularPath(path);
                DeploymentJournalOperation.SetStage(LockPath, $"DeletingWorkDirectory {id}");
                Directory.Delete(path, true);
            }
        }
    }, save: false, nameof(Cleanup), cancellationToken);

    public bool RestartRequested => Read().Batch?.Phase == "Approved";
}
