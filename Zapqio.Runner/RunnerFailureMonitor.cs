namespace Zapqio.Runner;

/// <summary>Distinguishes a failed background worker from an intentional host shutdown.</summary>
public sealed class RunnerFailureMonitor : IDisposable
{
    private readonly BackgroundService[] _workers;
    private readonly ILogger<RunnerFailureMonitor> _logger;
    private readonly Action<int>? _terminateProcess;
    private readonly CancellationTokenRegistration _stopping;

    public RunnerFailureMonitor(IEnumerable<IHostedService> services, IHostApplicationLifetime lifetime,
        ILogger<RunnerFailureMonitor> logger, Action<int>? terminateProcess = null)
    {
        _workers = services.OfType<BackgroundService>().ToArray();
        _logger = logger;
        _terminateProcess = terminateProcess;
        _stopping = lifetime.ApplicationStopping.Register(OnStopping);
    }

    public int ExitCode => _workers.Any(worker => worker.ExecuteTask?.IsFaulted == true) ? 1 : 0;

    private void OnStopping()
    {
        var failed = _workers.FirstOrDefault(worker => worker.ExecuteTask?.IsFaulted == true);
        if (failed is null)
        {
            return;
        }

        _logger.LogCritical(failed.ExecuteTask!.Exception,
            "Runner background service {Service} failed; process exit code=1", failed.GetType().Name);

        // A Windows Service must exit BEFORE SCM receives a clean Stopped status.
        // Merely returning 1 from Main after WaitForShutdownAsync is too late.
        // Console/systemd hosts can finish their normal shutdown and return ExitCode.
        _terminateProcess?.Invoke(1);
    }

    public void Dispose() => _stopping.Dispose();
}
