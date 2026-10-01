using System.Diagnostics;
using System.Text;
using Zapqio.Deployments;

namespace Zapqio.Runner.Deployments;

public interface IDeploymentBuild
{
    Task<byte[]> PublishAsync(DecodedBundle bundle, string work, long maxBytes, int maxFiles, CancellationToken ct);
}
public interface IDeploymentModules
{
    void Load(IReadOnlyList<InstalledModule> installed, bool validate);
}

public sealed class DeploymentInstaller(DeploymentStore store, string modulesRoot, IDeploymentBuild build, IDeploymentModules modules,
    ILogger<DeploymentInstaller>? logger = null)
{
    private readonly string _modules = Path.GetFullPath(modulesRoot);
    private string ModulePath(string name)
    {
        DeploymentBundle.ValidatePackageName(name);
        var path = Path.Combine(_modules, name + ".zip");
        DeploymentBundle.RequireRegularPath(path);
        return path;
    }

    // True requires a fresh process: assemblies may already have entered the default load context.
    public async Task<bool> StartAsync(CancellationToken ct)
    {
        var state = store.Read();
        if (state.Batch is { Phase: "Preparing" or "Switching" or "Loading" } interrupted)
        {
            if (interrupted.Phase is "Switching" or "Loading")
            {
                Restore(interrupted);
            }

            store.Finish("Failed", "Installation was interrupted; the complete previous set was restored.", interrupted.Previous);
            Cleanup(interrupted.Approval.BatchId);
            state = store.Read();
        }

        if (state.Batch is not { Phase: "Approved" } batch)
        {
            modules.Load(state.Installed, false);
            return false;
        }

        var loadingStarted = false;
        var switchingStarted = false;
        var work = store.WorkPath(batch.Approval.BatchId);
        DeploymentBundle.RequireRegularPath(work);
        Directory.CreateDirectory(work);
        store.Edit(s =>
        {
            s.Batch!.Previous = s.Installed.ToList();
            s.Batch.Phase = "Preparing";
        });
        try
        {
            var selected = batch.Approval.Packages.Select(p => state.Packages.Single(d => d.Notice.DeploymentId == p.DeploymentId)).ToList();
            if (selected.Select(p => p.Manifest!.PackageName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            {
                throw new InvalidDataException("Batch contains conflicting package names.");
            }

            var prepared = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in selected)
            {
                ct.ThrowIfCancellationRequested();
                var approval = batch.Approval.Packages.Single(a => a.DeploymentId == p.Notice.DeploymentId);
                var decoded = DeploymentBundle.Read(await File.ReadAllBytesAsync(store.BundlePath(approval.DeploymentId), ct),
                    approval.BundleSha256, p.Notice.MaxBytes, p.Notice.MaxFiles);
                if (decoded.Manifest != p.Manifest &&
                    System.Text.Json.JsonSerializer.Serialize(decoded.Manifest, DeploymentJson.Options) != System.Text.Json.JsonSerializer.Serialize(p.Manifest, DeploymentJson.Options))
                {
                    throw new InvalidDataException("Approved package metadata changed.");
                }

                if (decoded.Manifest.Action == "Withdraw")
                {
                    continue;
                }

                var bytes = decoded.Manifest.ContentKind == "DotnetSource"
                    ? await build.PublishAsync(decoded, Path.Combine(work, p.Notice.DeploymentId.ToString("N")), p.Notice.MaxBytes, p.Notice.MaxFiles, ct)
                    : decoded.Payload;
                DeploymentBundle.ValidateModule(DeploymentBundle.ReadZip(bytes, p.Notice.MaxBytes, p.Notice.MaxFiles));
                prepared.Add(decoded.Manifest.PackageName, bytes);
            }
            var backups = new List<ModuleBackup>();
            var backupRoot = Path.Combine(work, "backup");
            Directory.CreateDirectory(backupRoot);
            foreach (var p in selected)
            {
                var name = p.Manifest!.PackageName;
                var path = ModulePath(name);
                if (File.Exists(path))
                {
                    var previous = await File.ReadAllBytesAsync(path, ct);
                    DeploymentBundle.AtomicWrite(Path.Combine(backupRoot, name + ".zip"), previous);
                    backups.Add(new(name, true, DeploymentBundle.Hash(previous)));
                }
                else
                {
                    backups.Add(new(name, false, null));
                }
            }
            // Persist every backup and the full rollback plan before the first replacement.
            store.Edit(s =>
            {
                s.Batch!.Backups = backups;
                s.Batch.Phase = "Switching";
            });
            switchingStarted = true;
            var installed = state.Installed.ToList();
            foreach (var p in selected)
            {
                var m = p.Manifest!;
                var path = ModulePath(m.PackageName);
                installed.RemoveAll(i => i.Manifest.RepositoryId == m.RepositoryId || string.Equals(i.Manifest.PackageName, m.PackageName, StringComparison.OrdinalIgnoreCase));
                if (m.Action == "Withdraw")
                {
                    File.Delete(path);
                }
                else
                {
                    var bytes = prepared[m.PackageName];
                    DeploymentBundle.AtomicWrite(path, bytes);
                    installed.Add(new(m, DeploymentBundle.Hash(bytes)));
                }
            }
            store.Edit(s => s.Batch!.Phase = "Loading");
            loadingStarted = true;
            modules.Load(installed, true);
            store.Finish("Applied", null, installed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var latest = store.Read().Batch!;
            if (switchingStarted)
            {
                Restore(latest); // A restore failure stops startup; never advertise a partial set.
            }

            var reason = ex is OperationCanceledException ? "Installation was interrupted." : ex.Message;
            store.Finish("Failed", reason.Length > 2000 ? reason[..2000] : reason, latest.Previous);
            if (!loadingStarted)
            {
                modules.Load(latest.Previous, false);
            }

            Cleanup(batch.Approval.BatchId);
            return loadingStarted;
        }
        Cleanup(batch.Approval.BatchId);
        return false;
    }

    private void Restore(LocalBatch batch)
    {
        foreach (var backup in batch.Backups)
        {
            var path = ModulePath(backup.PackageName);
            if (!backup.Existed)
            {
                File.Delete(path);
            }
            else
            {
                var source = Path.Combine(store.WorkPath(batch.Approval.BatchId), "backup", backup.PackageName + ".zip");
                DeploymentBundle.RequireRegularPath(source);
                var bytes = File.ReadAllBytes(source);
                if (DeploymentBundle.Hash(bytes) != backup.Sha256)
                {
                    throw new InvalidDataException("Rollback backup checksum mismatch.");
                }

                DeploymentBundle.AtomicWrite(path, bytes);
            }
        }
    }

    private void Cleanup(Guid batch)
    {
        var path = Path.GetFullPath(store.WorkPath(batch));
        var parent = Path.GetFullPath(Path.Combine(store.Root, "work")) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Work path escaped deployment root.");
        }

        DeploymentBundle.RequireRegularPath(path);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Temporary deployment files will be cleaned on a later pass");
        }
    }
}

public sealed class DotnetDeploymentBuild : IDeploymentBuild
{
    public async Task<byte[]> PublishAsync(DecodedBundle bundle, string work, long maxBytes, int maxFiles, CancellationToken ct)
    {
        var source = Path.Combine(work, "source");
        var output = Path.Combine(work, "output");
        var files = DeploymentBundle.ReadZip(bundle.Payload, maxBytes, maxFiles);
        DeploymentBundle.Extract(files, source);
        Directory.CreateDirectory(output);
        var project = files.Keys.Single(p => !p.Contains('/') && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = source,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "publish", Path.Combine(source, project), "-c", "Release", "-o", Path.Combine(output, "publish"), "--nologo" })
        {
            start.ArgumentList.Add(arg);
        }
        // Runner credentials are never inherited by build targets or child processes.
        start.Environment.Remove("ZAPQIO_TOKEN");
        using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet publish.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var log = new StringBuilder();
        var sync = new object();
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                lock (sync)
                {
                    log.Append(buffer, 0, count);
                    if (log.Length > 65536)
                    {
                        log.Remove(0, log.Length - 65536);
                    }
                }
            }
        }
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
            }
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("dotnet publish failed: " + log);
        }

        // The module project may package itself with ZipAfterPublish. Otherwise publish must
        // include ##Dll/##Shared, which are validated by the same module ZIP validator.
        var archives = Directory.GetFiles(output, "*.zip", SearchOption.TopDirectoryOnly);
        if (archives.Length > 1)
        {
            throw new InvalidDataException("Publish produced more than one module ZIP.");
        }

        if (archives.Length == 1)
        {
            DeploymentBundle.RequireRegularPath(archives[0]);
            if (new FileInfo(archives[0]).Length > maxBytes)
            {
                throw new InvalidDataException("Published ZIP exceeds the size limit.");
            }

            return await File.ReadAllBytesAsync(archives[0], ct);
        }

        var publish = Path.Combine(output, "publish");
        var published = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void Visit(string dir)
        {
            DeploymentBundle.RequireRegularPath(dir);
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                DeploymentBundle.RequireRegularPath(entry.FullName);
                if (entry is DirectoryInfo)
                {
                    Visit(entry.FullName);
                }
                else
                {
                    var file = (FileInfo)entry;
                    total += file.Length;
                    if (total > maxBytes || published.Count >= maxFiles)
                    {
                        throw new InvalidDataException("Published module exceeds the configured limits.");
                    }

                    published.Add(Path.GetRelativePath(publish, file.FullName).Replace('\\', '/'), File.ReadAllBytes(file.FullName));
                }
            }
        }
        Visit(publish);
        DeploymentBundle.ValidateModule(published);
        return DeploymentBundle.Zip(published);
    }
}
