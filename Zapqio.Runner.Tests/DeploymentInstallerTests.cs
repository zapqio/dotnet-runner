using Zapqio.Deployments;
using Zapqio.Runner.Deployments;

namespace Zapqio.Runner.Tests;

public sealed class DeploymentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sr3-install-" + Guid.NewGuid().ToString("N"));
    private DeploymentStore Store => new(Path.Combine(_root, "Deployments"));
    private string Modules => Path.Combine(_root, "Modules");
    private sealed class Build : IDeploymentBuild
    {
        public int Calls;
        public bool Fail;
        public Task<byte[]> PublishAsync(DecodedBundle bundle, string work, long maxBytes, int maxFiles, CancellationToken ct)
        {
            Calls++;
            if (Fail)
            {
                throw new InvalidOperationException("compile error");
            }

            return Task.FromResult(Module(9));
        }
    }
    private sealed class Loader : IDeploymentModules
    {
        public int Calls;
        public bool Fail;
        public void Load(IReadOnlyList<InstalledModule> installed, bool validate)
        {
            Calls++;
            if (Fail && validate)
            {
                throw new InvalidOperationException("DLL constructor failed");
            }
        }
    }
    private static byte[] Module(byte value) => DeploymentBundle.Zip(new Dictionary<string, byte[]>
    { ["##Dll"] = "module.dll"u8.ToArray(), ["module.dll"] = [value] });
    private void Add(string name, bool source = false, bool withdraw = false)
    {
        var m = new DeploymentManifest
        {
            DeploymentId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            SnapshotId = Guid.NewGuid(),
            Sequence = 1,
            PackageName = name,
            Commit = new string('a', 40),
            Action = withdraw ? "Withdraw" : "Apply",
            ContentKind = source ? "DotnetSource" : "DotnetModuleZip"
        };
        var payload = withdraw ? [] : source ? DeploymentBundle.Zip(new Dictionary<string, byte[]> { ["module.csproj"] = "<Project/>"u8.ToArray() }) : Module(9);
        var bytes = DeploymentBundle.Create(m, payload, 100000, 20);
        var n = new DeploymentNotice
        {
            DeploymentId = m.DeploymentId,
            RepositoryId = m.RepositoryId,
            Sequence = 1,
            BundleUrl = "bundle",
            BundleSha256 = DeploymentBundle.Hash(bytes),
            BundleBytes = bytes.Length,
            MaxBytes = 100000,
            MaxFiles = 20
        };
        Store.Observe(n);
        Store.Receive(n, bytes);
    }
    private void Approve()
    {
        var request = Store.RequestApproval(null);
        Store.ApprovalReceived(new(request.BatchId, true, null));
    }
    private void Old(string name, byte value)
    {
        Directory.CreateDirectory(Modules);
        File.WriteAllBytes(Path.Combine(Modules, name + ".zip"), Module(value));
    }

    [Fact]
    public async Task Source_is_neither_built_nor_installed_before_the_web_ack()
    {
        Add("a", source: true);
        Store.RequestApproval(null);
        var build = new Build();
        Assert.False(await new DeploymentInstaller(Store, Modules, build, new Loader()).StartAsync(default));
        Assert.Equal(0, build.Calls);
        Assert.False(Directory.Exists(Modules));
        Assert.Equal("Requested", Store.Read().Batch!.Phase);
    }
    [Fact]
    public async Task A_build_failure_keeps_every_previous_zip_and_does_not_require_an_extra_restart()
    {
        Old("a", 1);
        Old("b", 2);
        Add("a");
        Add("b", source: true);
        Approve();
        var loader = new Loader();
        Assert.False(await new DeploymentInstaller(Store, Modules, new Build { Fail = true }, loader).StartAsync(default));
        Assert.Equal(Module(1), File.ReadAllBytes(Path.Combine(Modules, "a.zip")));
        Assert.Equal(Module(2), File.ReadAllBytes(Path.Combine(Modules, "b.zip")));
        Assert.Equal(1, loader.Calls);
        Assert.All(Store.Read().Reports.Single(r => r.BatchId is not null).Items, i => Assert.Equal("Failed", i.Status));
    }
    [Fact]
    public async Task A_load_failure_restores_updated_and_withdrawn_packages_and_requires_a_fresh_process()
    {
        Old("a", 1);
        Old("b", 2);
        Add("a");
        Add("b", withdraw: true);
        Add("new");
        Approve();
        var loader = new Loader { Fail = true };
        Assert.True(await new DeploymentInstaller(Store, Modules, new Build(), loader).StartAsync(default));
        Assert.Equal(Module(1), File.ReadAllBytes(Path.Combine(Modules, "a.zip")));
        Assert.Equal(Module(2), File.ReadAllBytes(Path.Combine(Modules, "b.zip")));
        Assert.False(File.Exists(Path.Combine(Modules, "new.zip")));
        Assert.Equal(1, loader.Calls); // Never attempt hot rollback in the contaminated process.
        Assert.False(await new DeploymentInstaller(Store, Modules, new Build(), new Loader()).StartAsync(default));
        Assert.Equal("Failed", Store.Read().Batch!.Phase);
    }
    [Fact]
    public async Task Success_persists_the_entire_version_map_and_only_one_batch_report()
    {
        Old("a", 1);
        Add("a");
        Add("b", source: true);
        Approve();
        Assert.False(await new DeploymentInstaller(Store, Modules, new Build(), new Loader()).StartAsync(default));
        var state = Store.Read();
        Assert.Equal(2, state.Installed.Count);
        Assert.All(state.Installed, i => Assert.Equal(DeploymentBundle.Hash(Module(9)), i.ZipSha256));
        Assert.Equal(2, Assert.Single(state.Reports).Items.Count);
        Assert.Equal("Applied", state.Batch!.Phase);
        Assert.False(Directory.Exists(Store.WorkPath(state.Batch.Approval.BatchId)));
        // Also clean a terminal batch if the process died between its durable result and cleanup.
        Directory.CreateDirectory(Store.WorkPath(state.Batch.Approval.BatchId));
        File.WriteAllText(Path.Combine(Store.WorkPath(state.Batch.Approval.BatchId), "leftover"), "temporary");
        Store.Cleanup();
        Assert.False(Directory.Exists(Store.WorkPath(state.Batch.Approval.BatchId)));
    }
    [Fact]
    public async Task Crash_during_switching_restores_all_files_before_loading_any_assembly()
    {
        Old("a", 1);
        Add("a");
        Approve();
        var batch = Store.Read().Batch!;
        var backup = Path.Combine(Store.WorkPath(batch.Approval.BatchId), "backup", "a.zip");
        DeploymentBundle.AtomicWrite(backup, Module(1));
        Store.Edit(s =>
        {
            s.Batch!.Backups = [new("a", true, DeploymentBundle.Hash(Module(1)))];
            s.Batch.Phase = "Switching";
        });
        Old("a", 9);
        var loader = new Loader();
        Assert.False(await new DeploymentInstaller(Store, Modules, new Build(), loader).StartAsync(default));
        Assert.Equal(Module(1), File.ReadAllBytes(Path.Combine(Modules, "a.zip")));
        Assert.Equal(1, loader.Calls);
        Assert.Equal("Failed", Store.Read().Batch!.Phase);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
