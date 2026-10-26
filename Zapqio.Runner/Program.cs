using Serilog;
using Microsoft.Extensions.Hosting.WindowsServices;
using System.Runtime.InteropServices;
using Zapqio.Runner.Background;
using Zapqio.Runner.Core;

namespace Zapqio.Runner
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (Deployments.WindowsDeploymentCli.IsCommand(args))
            {
                return await Deployments.WindowsDeploymentCli.RunAsync(args);
            }

            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService();
            builder.Services.AddSystemd();
            builder.Services.AddSingleton(x =>
            {
                var s = new AppSettings();
                builder.Configuration.Bind(s);
                var envKey = builder.Configuration.GetValue<string>("ZAPQIO_TOKEN");
                var envId = builder.Configuration.GetValue<string>("ZAPQIO_NAME");
                var envUrl = builder.Configuration.GetValue<string>("ZAPQIO_URL");
                var envMaxConcurrency = builder.Configuration.GetValue<string>("ZAPQIO_MAX_CONCURRENCY");
                var envMinLogLevel = builder.Configuration.GetValue<string>("ZAPQIO_MIN_LOG_LEVEL");
                if (!string.IsNullOrEmpty(envKey))
                {
                    s.Token = envKey;
                }

                if (!string.IsNullOrEmpty(envId))
                {
                    s.Name = envId;
                }

                if (string.IsNullOrEmpty(s.Name))
                {
                    var filePath = Path.Combine(builder.Environment.ContentRootPath, "##Name");
                    if (File.Exists(filePath))
                    {
                        s.Name = File.ReadAllText(filePath);
                    }

                    if (string.IsNullOrEmpty(s.Name))
                    {
                        s.Name = Guid.NewGuid().ToString();
                        File.WriteAllText(filePath, s.Name);
                    }
                }

                if (!string.IsNullOrEmpty(envUrl))
                {
                    s.Url = envUrl;
                }

                if (int.TryParse(envMaxConcurrency, out var maxConcurrency))
                {
                    s.MaxConcurrency = maxConcurrency;
                }

                if (!string.IsNullOrEmpty(envMinLogLevel))
                {
                    s.MinRemoteLogLevel = envMinLogLevel;
                }

                s.Normalize();
                return s;
            });
            // Configure the logger during DI construction, before the host captures its ILogger.
            var originalConsoleOut = Console.Out;
            builder.Services.AddSerilog((services, log) =>
                RunnerLogging.Configure(log, services.GetRequiredService<AppSettings>().Logger, originalConsoleOut));

            // Kolejka wyjściowa i jej jedyny autor. Wszystko, co idzie do platformy, przechodzi tędy.
            builder.Services.AddSingleton(sp => new Outbox(sp.GetRequiredService<AppSettings>().MaxQueuedLogLines));

            // Wszystkie trzy kanały modułu (stdout/stderr, RunnerLog, wstrzyknięty ILogger) schodzą się
            // w JobLogWriter - to on zna próg wysyłki i przypisanie wpisu do zadania.
            builder.Services.AddSingleton<JobLogWriter>();
            builder.Services.AddSingleton<ScopedConsole>();
            builder.Services.AddSingleton(sp => new MethodsProvider(sp.GetRequiredService<ILogger<MethodsProvider>>(),
                sp.GetRequiredService<JobLogWriter>(), deferred: true));
            builder.Services.AddSingleton(services => new Deployments.DeploymentStore(
                Path.Combine(AppContext.BaseDirectory, "Deployments"), services.GetRequiredService<ILogger<Deployments.DeploymentStore>>()));
            builder.Services.AddSingleton<Deployments.DeploymentCoordinator>();
            builder.Services.AddSingleton<WSClient>();
            builder.Services.AddSingleton<RunnerProcessState>();
            builder.Services.AddSingleton<IOutboundTransport>(sp => sp.GetRequiredService<WSClient>());
            builder.Services.AddSingleton<OutboundSender>();
            builder.Services.AddSingleton<PendingJobReturns>();
            builder.Services.AddSingleton<ExecuteJob>();
            builder.Services.AddSingleton(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                var client = sp.GetRequiredService<WSClient>();
                var execute = sp.GetRequiredService<ExecuteJob>();
                return new JobScheduler(
                    settings.MaxConcurrency,
                    execute.Exec,
                    job => client.SendJobAccepted(job.Id, job.AttemptId),
                    client.SendQueryOnJob,
                    sp.GetRequiredService<ILogger<JobScheduler>>(),
                    sp.GetRequiredService<RunnerProcessState>());
            });

            // Kolejność rejestracji to odwrotność kolejności zatrzymania: pętla połączenia staje
            // pierwsza i czeka na zadania w toku oraz wysyłkę ich wyników, planista drugi, a nadawca
            // gniazda żyje najdłużej - inaczej wyniki nie miałyby czym wyjść.
            builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboundSender>());
            builder.Services.AddHostedService<JobSchedulerHost>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Deployments.DeploymentCoordinator>());
            builder.Services.AddHostedService<RequestBindBackground>();

            // Host ma dać pętli połączenia czas na łagodne zatrzymanie (StopTimeoutSeconds) z zapasem
            // na uzgodnienie zamknięcia gniazda; domyślne 30 s hosta ucięłoby dłuższe oczekiwanie.
            builder.Services.AddOptions<HostOptions>().Configure<AppSettings>((options, settings) =>
                options.ShutdownTimeout = TimeSpan.FromSeconds(settings.StopTimeoutSeconds + 15));

            using var host = builder.Build();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            using var stoppingLog = lifetime.ApplicationStopping.Register(() =>
                logger.LogWarning("Runner shutdown requested; stopping hosted services"));
            using var stoppedLog = lifetime.ApplicationStopped.Register(() =>
                logger.LogInformation("Runner hosted services stopped"));
            Action<int>? terminateOnFailure = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()
                ? Environment.Exit : null;
            try
            {
                using var failures = new RunnerFailureMonitor(host.Services.GetServices<IHostedService>(), lifetime,
                    host.Services.GetRequiredService<ILogger<RunnerFailureMonitor>>(), terminateOnFailure);
                logger.LogInformation("Runner starting: version={Version}, runtime={Runtime}, baseDirectory={BaseDirectory}, account={Account}",
                    typeof(Program).Assembly.GetName().Version, RuntimeInformation.FrameworkDescription,
                    AppContext.BaseDirectory, $"{Environment.UserDomainName}\\{Environment.UserName}");
                var settings = host.Services.GetRequiredService<AppSettings>();

                // Statyczny RunnerLog modułów musi mieć gdzie oddawać wpisy, zanim powstanie pierwszy
                // moduł - moduł potrafi logować już z konstruktora, a MethodsProvider buduje je
                // wszystkie przy pierwszym sięgnięciu po metody. Odbiornik żyje tak długo jak proces,
                // więc zwrócony uchwyt (służący tylko testom do przywrócenia poprzedniego) przepada.
                RunnerLog.UseSink(new ModuleLogSink(host.Services.GetRequiredService<JobLogWriter>()));

                logger.LogInformation(
                    "Log zadań: do platformy idą wpisy modułu od poziomu {Threshold} (ZAPQIO_MIN_LOG_LEVEL), kolejka {MaxQueued} linii",
                    settings.RemoteLogThreshold, settings.MaxQueuedLogLines);
                await host.StartAsync();
                await host.WaitForShutdownAsync();
                var exitCode = failures.ExitCode;
                logger.LogInformation("Runner host finished; process exit code={ExitCode}", exitCode);
                return exitCode;
            }
            catch (Exception ex)
            {
                logger.LogCritical(ex, "Runner startup or shutdown failed; exception will terminate the process");
                terminateOnFailure?.Invoke(1);
                throw;
            }
        }
    }
}
