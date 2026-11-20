using System.Diagnostics;
using System.Reflection;
using System.Text;

// Intentionally independent of dependency injection and the sensor database:
// fatal exceptions on library-created threads can terminate the process before
// hosted-service logging or the browser UI has initialized.
public static class CollectorStartupLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SensorDash", "Logs");
    private static readonly string LogPath = Path.Combine(DirectoryPath, "collector-startup.log");
    private const long MaximumBytes = 2 * 1024 * 1024;
    private static int _installed;
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // This is observational only. An unhandled exception from a third-party
            // thread still terminates the .NET process; service recovery handles it.
            Write("fatal", "Unhandled exception; terminating=" +
                args.IsTerminating + "; " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Write("error", "Unobserved task exception: " + args.Exception);
        Write("info", "Process starting; pid=" + Environment.ProcessId +
            "; version=" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown") +
            "; executable=" + Environment.ProcessPath);
    }

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumBytes)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, DateTimeOffset.UtcNow.ToString("O") +
                    " [" + level + "] " + message.Replace("\r", " ").Replace("\n", " ") +
                    Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never cause a cooling-service failure themselves.
        }
    }
}
