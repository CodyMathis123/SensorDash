using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A bounded in-memory handoff. The hardware owner never waits for SQLite.
// At 10-second history cadence, 512 queued frames provide ~85 minutes of
// short-term buffering. An extended disk failure can exceed that capacity:
// dropped archival frames are counted and explicitly reported, never hidden.
internal sealed class ArchiveWork(
    SensorFrame frame, DashboardSettings settings, bool saveHistory,
    FanRecoveryEvent[] coolingEvents)
{
    internal SensorFrame Frame { get; } = frame;
    internal DashboardSettings Settings { get; } = settings;
    internal bool SaveHistory { get; } = saveHistory;
    internal FanRecoveryEvent[] CoolingEvents { get; } = coolingEvents;
    internal bool SamplesSaved { get; set; } = !saveHistory;
    internal bool AccessorySaved { get; set; } = !saveHistory;
    internal bool ThresholdsChecked { get; set; } = !saveHistory;
    internal int CoolingEventsSaved { get; set; }
}

public sealed class ArchiveQueue
{
    internal const int DefaultCapacity = 512;
    private readonly Channel<ArchiveWork> _channel;
    private readonly int _capacity;
    private long _accepted, _dropped, _depth;

    internal ArchiveQueue(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
        _channel = Channel.CreateBounded<ArchiveWork>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    internal ChannelReader<ArchiveWork> Reader => _channel.Reader;
    internal int Capacity => _capacity;
    internal int Depth => (int)Math.Max(0, Interlocked.Read(ref _depth));
    internal void MarkDequeued() => Interlocked.Decrement(ref _depth);
    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long Dropped => Interlocked.Read(ref _dropped);

    internal bool TryEnqueue(SensorFrame frame, DashboardSettings settings,
        bool saveHistory, FanRecoveryEvent[] coolingEvents)
    {
        if (!saveHistory && coolingEvents.Length == 0) return true;
        var item = new ArchiveWork(frame, settings, saveHistory, coolingEvents);
        if (_channel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _accepted);
            Interlocked.Increment(ref _depth);
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}

// All archival writes and non-safety temperature notifications execute here,
// never on the single LHM hardware/PWM thread. The progress flags make a retry
// idempotent when one of several independent SQLite transactions fails.
internal sealed class ArchiveProcessor(HistoryStore database)
{
    private readonly Dictionary<string, bool> _alarms = new(StringComparer.Ordinal);

    internal void Process(ArchiveWork item)
    {
        if (!item.SamplesSaved)
        {
            database.Record(item.Frame, item.Settings);
            item.SamplesSaved = true;
        }
        if (!item.AccessorySaved)
        {
            database.RecordAccessoryPower(item.Frame, item.Settings);
            item.AccessorySaved = true;
        }
        if (!item.ThresholdsChecked)
        {
            CheckThresholds(item.Frame, item.Settings);
            item.ThresholdsChecked = true;
        }
        while (item.CoolingEventsSaved < item.CoolingEvents.Length)
        {
            var evt = item.CoolingEvents[item.CoolingEventsSaved];
            database.Event(evt.Severity, evt.Sensor, evt.Message, evt.Value);
            item.CoolingEventsSaved++;
        }
    }

    private void CheckThresholds(SensorFrame frame, DashboardSettings settings)
    {
        var byId = frame.Sensors.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var (id, threshold) in settings.HighThresholds)
        {
            if (!byId.TryGetValue(id, out var sensor) ||
                sensor.Value is not double value) continue;
            var active = _alarms.GetValueOrDefault(id);
            if (!active && value >= threshold)
            {
                database.Event("warning", id,
                    $"{settings.Aliases.GetValueOrDefault(id, sensor.Name)} reached {value:0.#} (threshold {threshold:0.#})",
                    value);
                _alarms[id] = true;
            }
            else if (active && value < threshold - 2)
            {
                database.Event("info", id,
                    $"{settings.Aliases.GetValueOrDefault(id, sensor.Name)} returned below threshold",
                    value);
                _alarms[id] = false;
            }
        }
    }
}

internal sealed class HistoryArchiveWorker(
    ArchiveQueue queue, HistoryStore database, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ArchiveProcessor _processor = new(database);
    private readonly ILogger _events = loggerFactory.CreateLogger(OperationalEvents.Category);
    private readonly OperationalOutage _outage = new(
        loggerFactory.CreateLogger(OperationalEvents.Category),
        "local history archival", OperationalEvents.ArchiveFailure,
        OperationalEvents.ArchiveFailureSummary, OperationalEvents.ArchiveRecovered);
    private DateTimeOffset _lastDropReport = DateTimeOffset.MinValue;
    private long _lastReportedDrops;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                queue.MarkDequeued();
                while (!stoppingToken.IsCancellationRequested)
                {
                    ReportOverflow();
                    try
                    {
                        _processor.Process(item);
                        _outage.Recovered();
                        break;
                    }
                    catch (Exception e)
                    {
                        // Retries preserve the current item; stages already
                        // committed to SQLite are not reinserted.
                        _outage.Failed(e, "archive-write");
                        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                        catch (OperationCanceledException) { return; }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void ReportOverflow()
    {
        var dropped = queue.Dropped;
        if (dropped == _lastReportedDrops) return;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastDropReport < TimeSpan.FromMinutes(15)) return;
        _events.LogWarning(OperationalEvents.ArchiveOverflow,
            "Local archive queue overflow: {DroppedTotal} snapshot/event batches could not be buffered; " +
            "Windows history and remote telemetry may contain gaps. Cooling hardware control remains active.",
            dropped);
        _lastReportedDrops = dropped;
        _lastDropReport = now;
    }
}
