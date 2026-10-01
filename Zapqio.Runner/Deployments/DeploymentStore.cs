using System.Text.Json;
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
public sealed class DeploymentStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string BundlePath(Guid id) => Path.Combine(Root, "bundles", id.ToString("N") + ".zip");
    public string WorkPath(Guid batch) => Path.Combine(Root, "work", batch.ToString("N"));

    public DeploymentState Read() => Access(s => s, save: false);
    public T Edit<T>(Func<DeploymentState, T> action) => Access(action, save: true);
    public void Edit(Action<DeploymentState> action) => Edit(s =>
    {
        action(s);
        return true;
    });

    private T Access<T>(Func<DeploymentState, T> action, bool save)
    {
        DeploymentBundle.RequireRegularPath(Root);
        Directory.CreateDirectory(Root);
        var lockPath = Path.Combine(Root, "state.lock");
        DeploymentBundle.RequireRegularPath(lockPath);
        FileStream? held = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (held is null)
        {
            try
            {
                held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
        using (held)
        {
            var path = Path.Combine(Root, "state.json");
            DeploymentBundle.RequireRegularPath(path);
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<DeploymentState>(File.ReadAllBytes(path), DeploymentJson.Options)
                    ?? throw new InvalidDataException("Deployment journal is empty.")
                : new DeploymentState();
            if (state.Version != 1)
            {
                throw new InvalidDataException("Unsupported deployment journal version.");
            }

            var result = action(state);
            if (save)
            {
                DeploymentBundle.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(state, DeploymentJson.Options));
            }

            return result;
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

    public void Cleanup() => Edit(s =>
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
                Directory.Delete(path, true);
            }
        }
    });

    public bool RestartRequested => Read().Batch?.Phase == "Approved";
}
