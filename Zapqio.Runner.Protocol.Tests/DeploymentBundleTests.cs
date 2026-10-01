using System.IO.Compression;
using System.Text;
using Zapqio.Deployments;

namespace Zapqio.Runner.Protocol.Tests;

public class DeploymentBundleTests
{
    [Fact]
    public void Bundle_preserves_identity_and_checks_both_archive_layers()
    {
        var payload = Zip(("##Dll", "Example.dll"), ("Example.dll", "binary"));
        var manifest = Manifest();
        var bundle = DeploymentBundle.Create(manifest, payload, 100000, 100);
        var decoded = DeploymentBundle.Read(bundle, DeploymentBundle.Hash(bundle), 100000, 100);
        Assert.Equal(manifest.DeploymentId, decoded.Manifest.DeploymentId);
        Assert.Equal(manifest.Commit, decoded.Manifest.Commit);
        Assert.Equal(payload, decoded.Payload);
        Assert.Equal(2, decoded.Manifest.Files.Count);
        Assert.Equal(DeploymentBundle.Hash(payload), decoded.Manifest.PayloadSha256);
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.Read(bundle, new string('0', 64), 100000, 100));
    }

    [Theory]
    [InlineData("../escaped.dll")]
    [InlineData("/absolute.dll")]
    [InlineData("C:/absolute.dll")]
    [InlineData("dir\\file.dll")]
    [InlineData("CON.dll")]
    [InlineData("lib/file:stream")]
    public void Unsafe_payload_is_rejected_before_approval(string path)
    {
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ReadZip(Zip((path, "bad")), 10000, 20));
    }

    [Fact]
    public void Case_collisions_links_and_expansion_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ReadZip(Zip(("a.dll", "a"), ("A.dll", "b")), 10000, 20));
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ReadZip(Zip(("a.dll", new string('a', 10000))), 1000, 20));
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ReadZip(Zip(("a", "a"), ("b", "b")), 10000, 1));
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside");
        }
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ReadZip(data.ToArray(), 10000, 20));
    }

    [Fact]
    public void Shared_library_without_methods_is_a_valid_module()
    {
        DeploymentBundle.ValidateModule(DeploymentBundle.ReadZip(Zip(("##Dll", ""), ("##Shared", "shared"), ("Sdk.dll", "library")), 10000, 20));
        Assert.Throws<InvalidDataException>(() => DeploymentBundle.ValidateModule(
            DeploymentBundle.ReadZip(Zip(("##Dll", "missing.dll")), 10000, 20)));
    }

    [Fact]
    public void Source_bundle_does_not_evaluate_project_or_build_targets()
    {
        var manifest = Manifest() with { ContentKind = "DotnetSource" };
        var payload = Zip(("Example.csproj", "<Project><Target Name=\"BeforeBuild\"><Exec Command=\"must-not-run\"/></Target></Project>"));
        var bytes = DeploymentBundle.Create(manifest, payload, 10000, 20);
        Assert.Equal("DotnetSource", DeploymentBundle.Read(bytes, DeploymentBundle.Hash(bytes), 10000, 20).Manifest.ContentKind);
    }

    public static DeploymentManifest Manifest() => new()
    {
        DeploymentId = Guid.NewGuid(),
        RepositoryId = Guid.NewGuid(),
        SnapshotId = Guid.NewGuid(),
        RepositoryName = "example",
        PackageName = "Example",
        Commit = new string('a', 40),
        Sequence = 1,
        Action = "Apply",
        ContentKind = "DotnetModuleZip"
    };

    public static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var data = new MemoryStream();
        using (var zip = new ZipArchive(data, ZipArchiveMode.Create, true))
        {
            foreach (var (path, text) in files)
            {
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return data.ToArray();
    }
}
