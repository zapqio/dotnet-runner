using Zapqio.Deployments;
using Zapqio.Runner.Deployments;

namespace Zapqio.Runner.Tests;

public sealed class DeploymentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sr3-store-" + Guid.NewGuid().ToString("N"));
    private DeploymentStore Store => new(_root);
    private (DeploymentNotice Notice, byte[] Bytes) Package(Guid repository, long seq)
    {
        var manifest = new DeploymentManifest
        {
            DeploymentId = Guid.NewGuid(),
            RepositoryId = repository,
            SnapshotId = Guid.NewGuid(),
            Sequence = seq,
            PackageName = "sample",
            Commit = new string('a', 40),
            ContentKind = "DotnetSource"
        };
        var bytes = DeploymentBundle.Create(manifest, DeploymentBundle.Zip(new Dictionary<string, byte[]>
        { ["sample.csproj"] = "<Project><Target Name='Evil' BeforeTargets='Build'/></Project>"u8.ToArray() }), 100000, 20);
        return (new DeploymentNotice
        {
            DeploymentId = manifest.DeploymentId,
            RepositoryId = repository,
            Sequence = seq,
            BundleUrl = $"runner/deployments/{manifest.DeploymentId}/bundle",
            BundleSha256 = DeploymentBundle.Hash(bytes),
            BundleBytes = bytes.Length,
            MaxBytes = 100000,
            MaxFiles = 20
        }, bytes);
    }
    [Fact]
    public void Download_and_request_do_not_authorize_execution_and_survive_process_restart()
    {
        var (notice, bytes) = Package(Guid.NewGuid(), 1);
        Assert.True(Store.Observe(notice));
        Assert.True(Store.Receive(notice, bytes));
        var request = Store.RequestApproval(null);
        Assert.False(Store.RestartRequested);
        Assert.False(Store.ApprovalReceived(new(Guid.NewGuid(), true, null)));
        Assert.Equal("Requested", Store.Read().Batch!.Phase);
        Assert.True(Store.ApprovalReceived(new(request.BatchId, true, null)));
        Assert.True(Store.RestartRequested);
        Assert.Equal("DotnetSource", Store.Read().Packages.Single().Manifest!.ContentKind);
    }
    [Fact]
    public void Late_download_cannot_replace_newer_submission_even_after_store_reopen()
    {
        var repo = Guid.NewGuid();
        var a = Package(repo, 1);
        var b = Package(repo, 2);
        Store.Observe(a.Notice);
        Store.Observe(b.Notice);
        Assert.False(Store.Receive(a.Notice, a.Bytes));
        Assert.True(Store.Receive(b.Notice, b.Bytes));
        Assert.False(Store.Observe(a.Notice));
        Assert.Equal(b.Notice.DeploymentId, Store.RequestApproval(null).Packages.Single().DeploymentId);
    }
    [Fact]
    public void Accepted_batch_is_immutable_when_newer_submission_arrives()
    {
        var repo = Guid.NewGuid();
        var a = Package(repo, 1);
        var b = Package(repo, 2);
        Store.Observe(a.Notice);
        Store.Receive(a.Notice, a.Bytes);
        var request = Store.RequestApproval(null);
        Store.Observe(b.Notice);
        Store.Receive(b.Notice, b.Bytes);
        Store.ApprovalReceived(new(request.BatchId, true, null));
        Assert.Equal(a.Notice.DeploymentId, Store.Read().Batch!.Approval.Packages.Single().DeploymentId);
        Assert.Throws<InvalidOperationException>(() => Store.RequestApproval(null));
        Assert.Throws<InvalidOperationException>(() => Store.Reject(a.Notice.DeploymentId, "no"));
    }
    [Fact]
    public void Only_matching_ack_removes_the_durable_report()
    {
        var a = Package(Guid.NewGuid(), 1);
        Store.Observe(a.Notice);
        Store.Receive(a.Notice, a.Bytes);
        Store.Acknowledge(new([new(a.Notice.DeploymentId, 99)]));
        Assert.Single(Store.Read().Reports);
        Store.Acknowledge(new([new(a.Notice.DeploymentId, 1)]));
        Assert.Empty(Store.Read().Reports);
    }
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
