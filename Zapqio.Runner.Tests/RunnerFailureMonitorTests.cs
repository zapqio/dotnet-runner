using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapqio.Runner.Tests;

public sealed class RunnerFailureMonitorTests
{
    private sealed class ControlledWorker : BackgroundService
    {
        public TaskCompletionSource Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Fail.Task.WaitAsync(stoppingToken);
            throw new InvalidDataException("Unrecoverable worker failure.");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_failure_returns_nonzero_and_service_exit_precedes_clean_stopped_status(bool windowsService)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var worker = new ControlledWorker();
        builder.Services.AddHostedService(_ => worker);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var exits = new List<int>();
        using var monitor = new RunnerFailureMonitor(host.Services.GetServices<IHostedService>(), lifetime,
            NullLogger<RunnerFailureMonitor>.Instance, windowsService ? code =>
            {
                Assert.False(lifetime.ApplicationStopped.IsCancellationRequested);
                exits.Add(code);
            } : null);

        await host.StartAsync(deadline.Token);
        var shutdown = host.WaitForShutdownAsync(deadline.Token);
        worker.Fail.SetResult();
        await shutdown;

        Assert.True(lifetime.ApplicationStopped.IsCancellationRequested);
        Assert.Equal(1, monitor.ExitCode);
        Assert.Equal(windowsService ? new[] { 1 } : Array.Empty<int>(), exits);
    }

    [Fact]
    public async Task Intentional_stop_and_worker_cancellation_leave_exit_code_zero()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var worker = new ControlledWorker();
        builder.Services.AddHostedService(_ => worker);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var exits = new List<int>();
        using var monitor = new RunnerFailureMonitor(host.Services.GetServices<IHostedService>(), lifetime,
            NullLogger<RunnerFailureMonitor>.Instance, exits.Add);

        await host.StartAsync(deadline.Token);
        var shutdown = host.WaitForShutdownAsync(deadline.Token);
        lifetime.StopApplication();
        await shutdown;

        Assert.Equal(0, monitor.ExitCode);
        Assert.Empty(exits);
        Assert.True(worker.ExecuteTask!.IsCanceled);
    }
}
