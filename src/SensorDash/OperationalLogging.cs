using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

internal static class OperationalEvents
{
    // Source is intentionally new: earlier releases registered SensorDash
    // under Application. Windows event sources cannot belong to two logs.
    internal const string LogName = "SensorDash";
    internal const string SourceName = "SensorDash.Collector";
    internal const string Category = "SensorDash.Operational";
    internal static readonly EventId CollectorStarted = new(1000, nameof(CollectorStarted));
    internal static readonly EventId CollectorStopping = new(1001, nameof(CollectorStopping));
    internal static readonly EventId SensorFailure = new(2001, nameof(SensorFailure));
    internal static readonly EventId SensorFailureSummary = new(2002, nameof(SensorFailureSummary));
    internal static readonly EventId SensorRecovered = new(2003, nameof(SensorRecovered));
    internal static readonly EventId ResumeReopenStarted = new(2004, nameof(ResumeReopenStarted));
    internal static readonly EventId ResumeReopenCompleted = new(2005, nameof(ResumeReopenCompleted));
    internal static readonly EventId ResumeRegistrationFailure = new(2006, nameof(ResumeRegistrationFailure));
    internal static readonly EventId ResumeHardwareCloseFailure = new(2007, nameof(ResumeHardwareCloseFailure));
    internal static readonly EventId AccessoryArchiveFailure = new(2008, nameof(AccessoryArchiveFailure));
    internal static readonly EventId CoolingEventArchiveFailure = new(3001, nameof(CoolingEventArchiveFailure));
    internal static readonly EventId CoolingGuardEntered = new(3002, nameof(CoolingGuardEntered));
    internal static readonly EventId CoolingGuardCleared = new(3003, nameof(CoolingGuardCleared));
    internal static readonly EventId ExportFailure = new(4001, nameof(ExportFailure));
    internal static readonly EventId ExportFailureSummary = new(4002, nameof(ExportFailureSummary));
    internal static readonly EventId ExportRecovered = new(4003, nameof(ExportRecovered));
    internal static readonly EventId ExportSettingsReloaded = new(4004, nameof(ExportSettingsReloaded));
    internal static readonly EventId ExportCheckpointRecovered = new(4005, nameof(ExportCheckpointRecovered));
    internal static readonly EventId RetentionCompleted = new(5001, nameof(RetentionCompleted));
    internal static readonly EventId RetentionFailure = new(5002, nameof(RetentionFailure));
    internal static readonly EventId RetentionFailureSummary = new(5003, nameof(RetentionFailureSummary));
    internal static readonly EventId RetentionRecovered = new(5004, nameof(RetentionRecovered));
    internal static readonly EventId ArchiveFailure = new(5101, nameof(ArchiveFailure));
    internal static readonly EventId ArchiveFailureSummary = new(5102, nameof(ArchiveFailureSummary));
    internal static readonly EventId ArchiveRecovered = new(5103, nameof(ArchiveRecovered));
    internal static readonly EventId ArchiveOverflow = new(5104, nameof(ArchiveOverflow));

    // Event Log is an operational timeline, not an HTTP trace. ASP.NET
    // routing, request processing, framework lifetime, and normal polling
    // information remain silent. Framework warnings/errors are retained.
    internal static bool Allows(string? category, LogLevel level) =>
        level != LogLevel.None &&
        (level >= LogLevel.Warning ||
         (string.Equals(category, Category, StringComparison.Ordinal) &&
          level >= LogLevel.Information));

    internal static void Configure(ILoggingBuilder logging)
    {
        // AddWindowsService installs a default Application EventLog provider.
        // Replace it rather than double-writing entries in two log channels.
        logging.ClearProviders();
        logging.AddEventLog(settings =>
        {
            settings.LogName = LogName;
            settings.SourceName = SourceName;
            settings.Filter = Allows;
        });
        logging.AddFilter<EventLogLoggerProvider>((category, level) => Allows(category, level));
    }
}

internal enum OutageSignal { None, FirstFailure, Summary, Recovered }
internal readonly record struct OutageNotice(OutageSignal Signal, int Failures,
    TimeSpan Duration, string ExceptionType, string Stage);

// Pure state machine. Tests can advance a fake clock without sleeping or
// touching the Windows Event Log. One first failure, one 15-minute summary
// per ongoing outage, and one recovery event.
internal sealed class OutageEpisode
{
    private static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(15);
    private DateTimeOffset? _started;
    private DateTimeOffset? _lastSummary;
    private int _failures;
    private string _lastException = "";
    private string _lastStage = "";

    internal OutageNotice Failure(DateTimeOffset now, string exceptionType, string stage)
    {
        var first = _started is null;
        if (first)
        {
            _started = now;
            _lastSummary = now;
            _failures = 0;
        }
        _failures++;
        _lastException = exceptionType;
        _lastStage = stage;
        var duration = now - _started!.Value;
        if (first) return new(OutageSignal.FirstFailure, _failures, duration, exceptionType, stage);
        if (now - _lastSummary!.Value < SummaryInterval)
            return new(OutageSignal.None, _failures, duration, exceptionType, stage);
        _lastSummary = now;
        return new(OutageSignal.Summary, _failures, duration, exceptionType, stage);
    }

    internal OutageNotice Recovery(DateTimeOffset now)
    {
        if (_started is not { } since) return default;
        var result = new OutageNotice(OutageSignal.Recovered, _failures,
            now - since, _lastException, _lastStage);
        Reset();
        return result;
    }

    internal void Reset()
    {
        _started = null;
        _lastSummary = null;
        _failures = 0;
        _lastException = "";
        _lastStage = "";
    }
}

internal sealed class OperationalOutage(ILogger log, string subsystem,
    EventId firstEvent, EventId summaryEvent, EventId recoveredEvent,
    LogLevel failureLevel = LogLevel.Warning)
{
    private readonly OutageEpisode _episode = new();

    internal void Failed(Exception exception, string stage)
    {
        var notice = _episode.Failure(DateTimeOffset.UtcNow, exception.GetType().Name, stage);
        if (notice.Signal == OutageSignal.FirstFailure)
            log.Log(failureLevel, firstEvent,
                "{Subsystem} failed at {Stage} ({FailureClass}); retrying. Further identical failures are aggregated.",
                subsystem, stage, notice.ExceptionType);
        else if (notice.Signal == OutageSignal.Summary)
            log.Log(failureLevel, summaryEvent,
                "{Subsystem} still failing after {DurationMinutes:0.0} minutes; {Failures} attempts; last stage={Stage}; last class={FailureClass}.",
                subsystem, notice.Duration.TotalMinutes, notice.Failures, stage, notice.ExceptionType);
    }

    internal void Recovered()
    {
        var notice = _episode.Recovery(DateTimeOffset.UtcNow);
        if (notice.Signal == OutageSignal.Recovered)
            log.LogInformation(recoveredEvent,
                "{Subsystem} recovered after {DurationSeconds:0.0}s and {Failures} failed attempts; last failure={FailureClass} at {Stage}.",
                subsystem, notice.Duration.TotalSeconds, notice.Failures,
                notice.ExceptionType, notice.Stage);
    }

    internal void Reset() => _episode.Reset();
}

internal static class OperationalLoggingSelfTest
{
    internal static void Run()
    {
        static void Check(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException("Operational logging: " + text);
        }
        Check(OperationalEvents.Allows(OperationalEvents.Category, LogLevel.Information), "operational information");
        Check(!OperationalEvents.Allows("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Information), "HTTP endpoint noise");
        Check(!OperationalEvents.Allows("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information), "HTTP request noise");
        Check(!OperationalEvents.Allows("TelemetryExportWorker", LogLevel.Information), "routine exporter info");
        Check(OperationalEvents.Allows("TelemetryExportWorker", LogLevel.Warning), "worker warnings");
        Check(OperationalEvents.Allows("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error), "framework errors");
        Check(!OperationalEvents.Allows(OperationalEvents.Category, LogLevel.Debug), "debug outside Event Log");

        var episode = new OutageEpisode();
        var start = new DateTimeOffset(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);
        Check(episode.Failure(start, "JsonException", "sample-cursor").Signal ==
              OutageSignal.FirstFailure, "first failure");
        for (var i = 1; i <= 252; i++)
            Check(episode.Failure(start.AddSeconds(3 * i), "JsonException", "sample-cursor").Signal ==
                  OutageSignal.None, "suppress rapid repeated retries");
        var summary = episode.Failure(start.AddMinutes(15), "JsonException", "sample-cursor");
        Check(summary.Signal == OutageSignal.Summary && summary.Failures == 254, "15 minute summary");
        Check(episode.Failure(start.AddMinutes(16), "JsonException", "sample-cursor").Signal ==
              OutageSignal.None, "no duplicate summary");
        var recovered = episode.Recovery(start.AddMinutes(17));
        Check(recovered.Signal == OutageSignal.Recovered && recovered.Failures == 255 &&
              recovered.ExceptionType == "JsonException" &&
              recovered.Stage == "sample-cursor", "one meaningful recovery");
        Check(episode.Recovery(start.AddMinutes(18)).Signal == OutageSignal.None, "recovery emitted once");
        episode.Failure(start.AddMinutes(19), "IOException", "delivery");
        episode.Reset();
        Check(episode.Recovery(start.AddMinutes(20)).Signal == OutageSignal.None, "disabled exporter has no false recovery");
        Check(OperationalEvents.CollectorStarted.Id == 1000 &&
              OperationalEvents.SensorRecovered.Id == 2003 &&
              OperationalEvents.ExportFailure.Id == 4001, "stable event ids");
    }
}
