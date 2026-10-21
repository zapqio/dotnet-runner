using Serilog;
using Serilog.Events;

namespace Zapqio.Runner;

internal static class RunnerLogging
{
    private const string Template = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} PID={ProcessId} {SourceContext}] {Message:lj}{NewLine}{Exception}";

    public static void Configure(LoggerConfiguration log, AppSettings.LoggerClass settings,
        TextWriter? console, string fileName = ".log", bool shared = false)
    {
        if (!Enum.TryParse<LogEventLevel>(settings.LogLevel, out var level))
        {
            level = LogEventLevel.Information;
        }

        log.MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId);
        if (console is not null)
        {
            log.WriteTo.TextWriter(console, outputTemplate: Template);
        }

        if (!string.IsNullOrEmpty(settings.PathDirectory))
        {
            var directory = Path.IsPathRooted(settings.PathDirectory)
                ? settings.PathDirectory : Path.Combine(AppContext.BaseDirectory, settings.PathDirectory);
            log.WriteTo.File(Path.Combine(directory, fileName), outputTemplate: Template, shared: shared,
                rollOnFileSizeLimit: true, fileSizeLimitBytes: 200 * 1048576, rollingInterval: RollingInterval.Day);
        }
    }

    public static Serilog.Core.Logger CreateCliLogger()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        var settings = configuration.GetSection("Logger").Get<AppSettings.LoggerClass>()
            ?? new AppSettings.LoggerClass { PathDirectory = "Logs" };
        var log = new LoggerConfiguration();
        // Keep stdout intact for `deploy show` JSON and avoid competing with the service's file sink.
        Configure(log, settings, console: null, fileName: "deployments-cli-.log", shared: true);
        return log.CreateLogger();
    }
}
