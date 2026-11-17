using System.Diagnostics.Eventing.Reader;

// Read-only, bounded SCM history. The service runs as LocalSystem but loss of
// access to the Windows Event Log must never affect hardware collection/PWM.
public sealed record CollectorScmEvent(DateTimeOffset At, int EventId, string Kind);
public sealed record CollectorHealthSummary(
    int UnexpectedTerminations7d,
    DateTimeOffset? LastUnexpectedTermination,
    DateTimeOffset? LastStartAfterCrash);
public sealed record CollectorHealthSnapshot(
    string State,
    int ProcessId,
    DateTimeOffset StartedAt,
    long UptimeSeconds,
    bool EventsAvailable,
    string? EventsWarning,
    int? UnexpectedTerminations7d,
    DateTimeOffset? LastUnexpectedTermination,
    DateTimeOffset? LastStartAfterCrash,
    IReadOnlyList<CollectorScmEvent> RecentEvents);

public static class CollectorHealth
{
    private const string ServiceName = "SensorDash Collector";
    private const int MaximumScannedEvents = 512;
    private const int MaximumDisplayedEvents = 12;
    private const long SevenDaysMilliseconds = 7L * 24 * 60 * 60 * 1000;

    public static CollectorHealthSnapshot Read()
    {
        var started = CollectorStartupLog.StartedAt;
        var uptime = Math.Max(0, (long)(DateTimeOffset.UtcNow - started).TotalSeconds);
        try
        {
            // Reverse order keeps the latest events if a machine has a noisy SCM log.
            // Query only SCM event IDs relevant to startup/failure, never arbitrary
            // application events or Windows credentials.
            var xpath = "*[System[(EventID=7031 or EventID=7034 or EventID=7036) " +
                "and TimeCreated[timediff(@SystemTime) <= " + SevenDaysMilliseconds + "]]]";
            var query = new EventLogQuery("System", PathType.LogName, xpath)
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            var events = new List<CollectorScmEvent>();
            for (var i = 0; i < MaximumScannedEvents; i++)
            {
                using var record = reader.ReadEvent();
                if (record is null) break;
                if (!string.Equals(record.ProviderName, "Service Control Manager",
                    StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsCollectorEvent(record)) continue;
                if (record.TimeCreated is not DateTime at) continue;
                var kind = EventKind(record);
                if (kind is null) continue;
                events.Add(new CollectorScmEvent(new DateTimeOffset(at), record.Id, kind));
            }
            var summary = Summarize(events);
            return new("running", Environment.ProcessId, started, uptime, true, null,
                summary.UnexpectedTerminations7d, summary.LastUnexpectedTermination,
                summary.LastStartAfterCrash, events.Take(MaximumDisplayedEvents).ToArray());
        }
        catch (Exception ex)
        {
            // EventLogReader can fail on log access, missing channels or malformed
            // records. Never turn an optional status panel into a service failure.
            CollectorStartupLog.Write("warning",
                "SCM health read unavailable: " + ex.GetType().Name);
            return new("running", Environment.ProcessId, started, uptime, false,
                "Windows Service Control Manager events could not be read.",
                null, null, null, []);
        }
    }

    private static bool IsCollectorEvent(EventRecord record)
    {
        try
        {
            // EventData[0] is the service display name for SCM 7031/7034/7036.
            var service = record.Properties.Count > 0
                ? record.Properties[0].Value?.ToString()
                : null;
            return string.Equals(service, ServiceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(service, "SensorDashCollector", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string? EventKind(EventRecord record)
    {
        if (record.Id is 7031 or 7034) return "unexpected-termination";
        if (record.Id != 7036) return null;
        try
        {
            var state = record.Properties.Count > 1
                ? record.Properties[1].Value?.ToString() : null;
            if (string.Equals(state, "running", StringComparison.OrdinalIgnoreCase))
                return "started";
            if (string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase))
                return "stopped";
        }
        catch { }
        return null;
    }

    // Pure event correlation: SCM start-after-failure is not proof that SCM,
    // rather than an operator, initiated the start.
    public static CollectorHealthSummary Summarize(IEnumerable<CollectorScmEvent> source)
    {
        var events = source.OrderByDescending(e => e.At).ToArray();
        var crashes = events.Where(e => e.Kind == "unexpected-termination").ToArray();
        var lastCrash = crashes.FirstOrDefault()?.At;
        DateTimeOffset? laterStart = null;
        if (lastCrash is DateTimeOffset failure)
        {
            laterStart = events.Where(e => e.Kind == "started" &&
                    e.At > failure && e.At <= failure.AddMinutes(15))
                .OrderBy(e => e.At).FirstOrDefault()?.At;
        }
        return new(crashes.Length, lastCrash, laterStart);
    }

    public static void SelfTest()
    {
        var t = new DateTimeOffset(2026, 11, 17, 11, 8, 20, TimeSpan.Zero);
        var events = new[]
        {
            new CollectorScmEvent(t.AddMinutes(1), 7036, "started"),
            new CollectorScmEvent(t, 7034, "unexpected-termination"),
            new CollectorScmEvent(t.AddMinutes(-30), 7036, "started")
        };
        var result = Summarize(events);
        if (result.UnexpectedTerminations7d != 1 ||
            result.LastUnexpectedTermination != t ||
            result.LastStartAfterCrash != t.AddMinutes(1))
            throw new InvalidOperationException("SCM recovery correlation test failed.");
        var noRecovery = Summarize(events.Skip(1));
        if (noRecovery.LastStartAfterCrash is not null)
            throw new InvalidOperationException("SCM manual/no-start classification test failed.");
        var distant = Summarize(new[]
        {
            new CollectorScmEvent(t.AddHours(1), 7036, "started"),
            new CollectorScmEvent(t, 7031, "unexpected-termination")
        });
        if (distant.LastStartAfterCrash is not null)
            throw new InvalidOperationException("Distant start must not imply recovery.");
        if (Summarize([]).UnexpectedTerminations7d != 0)
            throw new InvalidOperationException("Empty SCM log summary failed.");
    }
}
