using System.Security;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Zapqio.Deployments;
using Zapqio.Runner.Core;
using Zapqio.Runner.Deployments;

namespace Zapqio.Runner.Tests;

public sealed class DeploymentBuildTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Real_publish_and_constructor_run_only_after_ack_and_loader_decides_the_batch_result(bool constructorFails, bool zipAfterPublish)
    {
        var root = Path.Combine(Path.GetTempPath(), "sr3-real-build-" + Guid.NewGuid().ToString("N"));
        var modules = Directory.CreateDirectory(Path.Combine(root, "Modules"));
        var cache = Directory.CreateDirectory(Path.Combine(root, "Cache"));
        var store = new DeploymentStore(Path.Combine(root, "Deployments"));
        var buildMarker = Path.Combine(root, "build-marker");
        var loadMarker = Path.Combine(root, "load-marker");
        var assemblyName = "Smoke" + Guid.NewGuid().ToString("N");
        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><AssemblyName>{{assemblyName}}</AssemblyName><NuGetAudit>false</NuGetAudit></PropertyGroup>
              <ItemGroup><Reference Include="Zapqio.Runner.Module.Core"><HintPath>{{SecurityElement.Escape(typeof(IRunnerMethod).Assembly.Location)}}</HintPath><Private>false</Private></Reference>
              <Content Include="##Dll" CopyToPublishDirectory="Always" /></ItemGroup>
              <Target Name="ApprovalMarker" BeforeTargets="Build"><WriteLinesToFile File="{{SecurityElement.Escape(buildMarker)}}" Lines="approved" Overwrite="true" /></Target>
              {{(zipAfterPublish ? "<Target Name=\"ZipAfterPublish\" AfterTargets=\"Publish\"><ZipDirectory SourceDirectory=\"$(PublishDir)\" DestinationFile=\"$(PublishDir)../module.zip\" Overwrite=\"true\" /></Target>" : "")}}
            </Project>
            """;
        var source = $$"""
            using Zapqio.Runner.Core;
            public class Method : IRunnerMethod
            {
                public Method() { System.IO.File.WriteAllText({{JsonSerializer.Serialize(loadMarker)}}, "loaded"); {{(constructorFails ? "throw new InvalidOperationException(\"constructor probe\");" : "")}} }
                public string NameMethod() => "probe";
                public Type InData() => null;
                public Type OutData() => null;
                public Task<string> Run(string data) => Task.FromResult("version-a");
            }
            """;
        var manifest = new DeploymentManifest
        {
            DeploymentId = Guid.NewGuid(),
            RepositoryId = Guid.NewGuid(),
            SnapshotId = Guid.NewGuid(),
            Sequence = 1,
            PackageName = "probe",
            Commit = new string('a', 40),
            ContentKind = "DotnetSource"
        };
        var payload = DeploymentBundle.Zip(new Dictionary<string, byte[]>
        {
            ["Probe.csproj"] = Encoding.UTF8.GetBytes(project),
            ["Method.cs"] = Encoding.UTF8.GetBytes(source),
            ["##Dll"] = Encoding.UTF8.GetBytes(assemblyName + ".dll"),
            ["NuGet.Config"] = "<configuration><packageSources><clear/></packageSources></configuration>"u8.ToArray()
        });
        var bytes = DeploymentBundle.Create(manifest, payload, 10000000, 100);
        var notice = new DeploymentNotice
        {
            DeploymentId = manifest.DeploymentId,
            RepositoryId = manifest.RepositoryId,
            Sequence = 1,
            BundleUrl = "bundle",
            BundleSha256 = DeploymentBundle.Hash(bytes),
            BundleBytes = bytes.Length,
            MaxBytes = long.MaxValue - DeploymentBundle.MetadataLimit,
            MaxFiles = int.MaxValue
        };
        store.Observe(notice);
        store.Receive(notice, bytes);
        var approval = store.RequestApproval(null);
        Assert.False(File.Exists(buildMarker));
        Assert.False(File.Exists(loadMarker));
        Assert.False(store.RestartRequested);
        store.ApprovalReceived(new(approval.BatchId, true, null));
        var settings = new AppSettings();
        settings.Normalize();
        using var provider = new MethodsProvider(NullLogger<MethodsProvider>.Instance,
            new JobLogWriter(new Outbox(100), settings, NullLoggerFactory.Instance), modules, cache, deferred: true);
        Assert.False(File.Exists(loadMarker));
        var restart = await new DeploymentInstaller(store, modules.FullName, new DotnetDeploymentBuild(), provider).StartAsync(default);
        Assert.True(File.Exists(buildMarker), store.Read().Batch?.Reason);
        Assert.True(File.Exists(loadMarker), store.Read().Batch?.Reason);
        Assert.Equal(constructorFails, restart);
        Assert.Equal(constructorFails ? "Failed" : "Applied", store.Read().Batch!.Phase);
        if (!constructorFails)
        {
            var method = provider.GetMethod("probe");
            Assert.NotNull(method);
            Assert.Equal("version-a", await method.Run(""));
            Assert.Equal(manifest.DeploymentId, provider.VersionFor(method)!.DeploymentId);
            Assert.Equal(manifest.Commit, provider.VersionFor(method)!.Commit);
            var newer = notice with { DeploymentId = Guid.NewGuid(), Sequence = 2, BundleUrl = null };
            store.Observe(newer);
            Assert.Equal(manifest.DeploymentId, provider.VersionFor(method)!.DeploymentId);
        }
        else
        {
            Assert.False(File.Exists(Path.Combine(modules.FullName, "probe.zip")));
        }

        // The default load context holds DLLs until this test process exits; fixtures live only in TEMP.
        try
        {
            Directory.Delete(root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
