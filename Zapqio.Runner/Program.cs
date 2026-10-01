using Serilog;
using Serilog.Events;
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
            builder.Services.AddSerilog();

            // Kolejka wyjściowa i jej jedyny autor. Wszystko, co idzie do platformy, przechodzi tędy.
            builder.Services.AddSingleton(sp => new Outbox(sp.GetRequiredService<AppSettings>().MaxQueuedLogLines));

            // Wszystkie trzy kanały modułu (stdout/stderr, RunnerLog, wstrzyknięty ILogger) schodzą się
            // w JobLogWriter - to on zna próg wysyłki i przypisanie wpisu do zadania.
            builder.Services.AddSingleton<JobLogWriter>();
            builder.Services.AddSingleton<ScopedConsole>();
            builder.Services.AddSingleton(sp => new MethodsProvider(sp.GetRequiredService<ILogger<MethodsProvider>>(),
                sp.GetRequiredService<JobLogWriter>(), deferred: true));
            builder.Services.AddSingleton(new Deployments.DeploymentStore(Path.Combine(AppContext.BaseDirectory, "Deployments")));
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

            var host = builder.Build();
            PreRun(host).GetAwaiter().GetResult();
            await host.RunAsync();
            return 0;

            static async Task PreRun(IHost host)
            {
                var settings = host.Services.GetRequiredService<AppSettings>();

                if (!Enum.TryParse<LogEventLevel>(settings.Logger.LogLevel, out var logLevel))
                {
                    logLevel = LogEventLevel.Information;
                }

                var template = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {SourceContext}] {Message:lj}{NewLine}{Exception}";
                var originalConsoleOut = Console.Out; // Zapisz PRZED utworzeniem ScopedConsole
                var log = new LoggerConfiguration()
                .WriteTo.TextWriter(originalConsoleOut, outputTemplate: template)
                .MinimumLevel.Is(logLevel)
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning);
                if (!string.IsNullOrEmpty(settings.Logger.PathDirectory))
                {
                    string fullPath = Path.IsPathRooted(settings.Logger.PathDirectory) ? settings.Logger.PathDirectory : Path.Combine(AppContext.BaseDirectory, settings.Logger.PathDirectory);
                    log.WriteTo.File(Path.Combine(fullPath, ".log"), outputTemplate: template, rollOnFileSizeLimit: true, fileSizeLimitBytes: 200 * 1048576, rollingInterval: RollingInterval.Day);
                }

                Log.Logger = log.CreateLogger();

                // Statyczny RunnerLog modułów musi mieć gdzie oddawać wpisy, zanim powstanie pierwszy
                // moduł - moduł potrafi logować już z konstruktora, a MethodsProvider buduje je
                // wszystkie przy pierwszym sięgnięciu po metody. Odbiornik żyje tak długo jak proces,
                // więc zwrócony uchwyt (służący tylko testom do przywrócenia poprzedniego) przepada.
                RunnerLog.UseSink(new ModuleLogSink(host.Services.GetRequiredService<JobLogWriter>()));

                var settingsLogLevel = settings.RemoteLogThreshold;
                Log.Logger.Information(
                    "Log zadań: do platformy idą wpisy modułu od poziomu {Threshold} (ZAPQIO_MIN_LOG_LEVEL), kolejka {MaxQueued} linii",
                    settingsLogLevel, settings.MaxQueuedLogLines);
            }
        }
    }
}
