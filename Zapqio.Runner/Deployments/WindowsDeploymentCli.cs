using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace Zapqio.Runner.Deployments;

public interface IDeploymentRestart { void Start(); }

public static class WindowsDeploymentCli
{
    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "deployments" or "deploy";
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("Deployment approval currently supports Windows only.");
            }

            if (args.Length == 4 && args[1] == "__restart")
            {
                await RestartServiceAsync(int.Parse(args[2]), args[3]);
                return 0;
            }

            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                throw new UnauthorizedAccessException("Run this command from an elevated administrator terminal.");
            }

            var store = new DeploymentStore(Path.Combine(AppContext.BaseDirectory, "Deployments"));
            if (args.Length == 3 && args[1] == "show")
            {
                var id = Guid.Parse(args[2]);
                var state = store.Read();
                var package = state.Packages.SingleOrDefault(p => p.Notice.DeploymentId == id)
                    ?? throw new InvalidOperationException("Unknown deployment.");
                var previous = state.Installed.SingleOrDefault(i => i.Manifest.RepositoryId == package.Notice.RepositoryId);
                var before = (previous?.Manifest.Files ?? []).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                var after = (package.Manifest?.Files ?? []).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                var changes = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
                    .Select(path => new
                    {
                        Path = path,
                        Change = !before.ContainsKey(path) ? "Added" : !after.ContainsKey(path) ? "Removed" :
                        before[path].Sha256 != after[path].Sha256 ? "Modified" : "Unchanged"
                    }).Where(c => c.Change != "Unchanged").ToArray();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    Package = package,
                    PreviousCommit = previous?.Manifest.Commit,
                    Changes = changes
                },
                    new System.Text.Json.JsonSerializerOptions(Zapqio.Deployments.DeploymentJson.Options) { WriteIndented = true }));
                return 0;
            }

            if (args.Length == 2 && args[1] is "list" or "pending")
            {
                var state = store.Read();
                if (state.Batch is { } batch)
                {
                    Console.WriteLine($"Batch {batch.Approval.BatchId}: {batch.Phase} {batch.Reason}");
                }

                foreach (var p in state.Packages.OrderBy(p => p.Manifest?.PackageName).ThenByDescending(p => p.Notice.Sequence))
                {
                    Console.WriteLine($"{p.Notice.DeploymentId}  {p.Status,-16} {p.Manifest?.PackageName}  {p.Manifest?.Action}  {p.Manifest?.Commit}  {p.Reason}");
                }
                return 0;
            }

            if (args.Length >= 3 && args[1] == "approve")
            {
                var service = ServiceName();
                using var controller = new ServiceController(service);
                if (controller.Status != ServiceControllerStatus.Running)
                {
                    throw new InvalidOperationException("The runner service must be running and connected to Web.");
                }

                var request = store.RequestApproval(args.Length == 3 && args[2] == "--all" ? null : args.Skip(2).Select(Guid.Parse).ToArray());
                Console.WriteLine($"Approval requested for {request.Packages.Count} package(s). Waiting for Web; approval will interrupt jobs and restart {service}.");
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    var batch = store.Read().Batch;
                    if (batch?.Approval.BatchId != request.BatchId)
                    {
                        throw new InvalidOperationException("Approval request changed.");
                    }

                    if (batch.Phase == "Denied")
                    {
                        throw new InvalidOperationException(batch.Reason ?? "Web rejected approval.");
                    }

                    if (batch.Phase != "Requested")
                    {
                        Console.WriteLine($"Web accepted the exact package set. State: {batch.Phase}. The service handles restart and installation.");
                        return 0;
                    }

                    await Task.Delay(250);
                }
                Console.Error.WriteLine("No Web acknowledgement yet. The request is retained and will be retried when connected. No installation may start before acknowledgement.");
                return 2;
            }

            if (args.Length == 3 && args[1] == "reject")
            {
                store.Reject(Guid.Parse(args[2]), "Rejected by the local administrator.");
                Console.WriteLine("Package rejected. The status will be sent to Web.");
                return 0;
            }

            Console.WriteLine("Usage: Zapqio.Runner deploy list | show <id> | approve <id> [id...] | approve --all | reject <id>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string ServiceName()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "##ServiceName");
        var name = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        if (name.Length is < 1 or > 256 || name.Any(char.IsControl) || name.Contains('/') || name.Contains('\\'))
        {
            throw new InvalidOperationException("Service identity is missing. Run the updated install.ps1 before approving deployments.");
        }

        return name;
    }

    public static void StartRestartHelper()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var service = ServiceName();
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Runner executable path is unavailable.");
        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }

        foreach (var arg in new[] { "deployments", "__restart", Environment.ProcessId.ToString(), service })
        {
            start.ArgumentList.Add(arg);
        }
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the restart helper.");
    }

    private static async Task RestartServiceAsync(int parentId, string service)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        if (service != ServiceName())
        {
            throw new InvalidOperationException("Restart target differs from the installed service.");
        }

        try
        {
            using var controller = new ServiceController(service);
            if (controller.Status is not ServiceControllerStatus.Stopped and not ServiceControllerStatus.StopPending)
            {
                controller.Stop();
            }

            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            try
            {
                using var parent = Process.GetProcessById(parentId);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await parent.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException)
            {
            }
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
        }
        catch (Exception ex)
        {
            var log = Path.Combine(AppContext.BaseDirectory, "Deployments", "restart.log");
            File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {ex.Message}{Environment.NewLine}");
            throw;
        }
    }
}
