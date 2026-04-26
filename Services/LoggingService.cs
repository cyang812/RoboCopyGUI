using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace RoboCopyGUI.Services;

/// <summary>
/// Configures Serilog with a rolling daily file sink under <c>logs/</c> next to the
/// executable. Log level can be controlled via <see cref="AppSettings.LogLevel"/>.
/// </summary>
public static class LoggingService
{
    /// <summary>Folder containing rolling log files. Created on first use.</summary>
    public static string LogDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Initialize(string levelText)
    {
        Directory.CreateDirectory(LogDirectory);

        LogEventLevel level = ParseLevel(levelText);
        string template =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.FromLogContext()
            .WriteTo.Debug(outputTemplate: template)
            .WriteTo.File(
                path: Path.Combine(LogDirectory, "robocopygui-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: template,
                shared: true)
            .CreateLogger();

        Log.Information("Logging initialized at level {Level}. Log directory: {Dir}",
            level, LogDirectory);
    }

    /// <summary>Reapply a new minimum level without restarting the app.</summary>
    public static void SetLevel(string levelText)
    {
        Log.Information("Reinitializing logger at new level: {Level}", levelText);
        Log.CloseAndFlush();
        Initialize(levelText);
    }

    public static void Shutdown() => Log.CloseAndFlush();

    private static LogEventLevel ParseLevel(string text) =>
        Enum.TryParse<LogEventLevel>(text, ignoreCase: true, out var lvl)
            ? lvl
            : LogEventLevel.Information;
}
