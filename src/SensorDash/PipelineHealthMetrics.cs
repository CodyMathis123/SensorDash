using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

// The time buckets are lifetime histograms for this collector process, not a
// rolling dashboard series. Every Observe call is bounded and allocation-free
// after its first hardware-family lookup. Reading diagnostics never touches LHM.
internal sealed record LatencySummary(
    long Samples, double? MeanMs, double? P95Ms, double? P99Ms, double? MaxMs);

internal sealed class LatencyHistogram
{
    private static readonly double[] BoundsMs =
        [0.25, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
    private readonly long[] _buckets = new long[BoundsMs.Length + 1];
    private long _count, _microseconds, _maxMicroseconds;

    internal void Observe(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0 ||
            milliseconds > 86_400_000) return;
        var microseconds = (long)Math.Round(milliseconds * 1000);
        var idx = Array.BinarySearch(BoundsMs, milliseconds);
        if (idx < 0) idx = ~idx;
        Interlocked.Increment(ref _buckets[idx]);
        Interlocked.Add(ref _microseconds, microseconds);
        Interlocked.Increment(ref _count);
        long previous;
        do
        {
            previous = Interlocked.Read(ref _maxMicroseconds);
            if (microseconds <= previous) break;
        } while (Interlocked.CompareExchange(
            ref _maxMicroseconds, microseconds, previous) != previous);
    }

    internal LatencySummary Snapshot()
    {
        var count = Interlocked.Read(ref _count);
        if (count == 0) return new(0, null, null, null, null);
        var counts = new long[_buckets.Length];
        for (var i = 0; i < counts.Length; i++)
            counts[i] = Interlocked.Read(ref _buckets[i]);
        var total = counts.Sum();
        if (total == 0) return new(0, null, null, null, null);
        double Quantile(double percentile)
        {
            var at = Math.Max(1, (long)Math.Ceiling(total * percentile));
            long seen = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen >= at)
                    return i < BoundsMs.Length ? BoundsMs[i] :
                        Interlocked.Read(ref _maxMicroseconds) / 1000.0;
            }
            return Interlocked.Read(ref _maxMicroseconds) / 1000.0;
        }
        return new(total, Math.Round(Interlocked.Read(ref _microseconds) / (1000.0 * count), 3),
            Quantile(.95), Quantile(.99),
            Math.Round(Interlocked.Read(ref _maxMicroseconds) / 1000.0, 3));
    }
}

internal sealed record CoolingInputRequirement(string SensorId, int MaxAgeSeconds);
internal sealed record CoolingInputSummary(int ConfiguredSources, int MissingSources,
    int StaleSources, double? OldestObservedAgeMs);

internal sealed record PipelineHealthSnapshot(
    string Window, DateTimeOffset CapturedUtc,
    LatencySummary HardwareScanMs, LatencySummary ControlMs,
    LatencySummary TotalCycleMs, LatencySummary IntervalMs,
    IReadOnlyDictionary<string, LatencySummary> HardwareFamiliesMs,
    long DelayedControlCycles, CoolingInputSummary CoolingInputs,
    int ArchiveQueueDepth, int ArchiveQueueCapacity, long ArchiveDropped,
    int FanTraceQueueDepth, int FanTraceQueueCapacity, long FanTraceDropped,
    double? SampleExportCursorLagSeconds, string ExportState,
    double? SecondsSinceLastDelivery);

// No SQLite reads, network calls, JSON serialization, event logging or disk
// writes on the physical hardware scan / PWM path.
public sealed class PipelineHealthMetrics
{
    private readonly LatencyHistogram _scan = new(), _control = new(),
        _total = new(), _interval = new();
    private readonly ConcurrentDictionary<string, LatencyHistogram> _families =
        new(StringComparer.Ordinal);
    private long _delayed, _lastExportCursorMs = -1;
    private int _configuredCooling, _missingCooling, _staleCooling;
    private long _oldestCoolingMs = -1;

    internal void ObserveScan(double milliseconds) => _scan.Observe(milliseconds);
    internal void ObserveControl(double milliseconds) => _control.Observe(milliseconds);
    internal void ObserveCycle(double milliseconds) => _total.Observe(milliseconds);

    internal void ObserveFamily(string family, double milliseconds)
    {
        if (family.Length is < 1 or > 40 || _families.Count > 32) return;
        _families.GetOrAdd(family, _ => new LatencyHistogram()).Observe(milliseconds);
    }

    internal void ObserveInterval(TimeSpan interval, int targetSeconds, bool resumed)
    {
        // Exclude intentional sleep/resume gaps from latency percentiles; the
        // hardware's resume/reopen path has its own operational events.
        if (resumed || interval <= TimeSpan.Zero || interval > TimeSpan.FromMinutes(1))
            return;
        _interval.Observe(interval.TotalMilliseconds);
        if (interval > TimeSpan.FromSeconds(Math.Max(2.5, targetSeconds * 2.5)))
            Interlocked.Increment(ref _delayed);
    }

    internal void ObserveCoolingInputs(SensorFrame frame,
        IReadOnlyList<CoolingInputRequirement> rules, DateTimeOffset now)
    {
        var missing = 0;
        var stale = 0;
        long oldest = -1;
        foreach (var rule in rules)
        {
            var sample = frame.Sensors.FirstOrDefault(x => x.Id == rule.SensorId);
            if (sample == null || sample.Value is not double v || !double.IsFinite(v) ||
                sample.ObservedAt is not DateTimeOffset timestamp)
            {
                missing++;
                continue;
            }
            var age = Math.Max(0, (long)(now - timestamp).TotalMilliseconds);
            if (age > rule.MaxAgeSeconds * 1000L) stale++;
            oldest = Math.Max(oldest, age);
        }
        Volatile.Write(ref _configuredCooling, rules.Count);
        Volatile.Write(ref _missingCooling, missing);
        Volatile.Write(ref _staleCooling, stale);
        Interlocked.Exchange(ref _oldestCoolingMs, oldest);
    }

    internal void SetExportCursor(long timestampMs) =>
        Interlocked.Exchange(ref _lastExportCursorMs, timestampMs);

    internal PipelineHealthSnapshot Snapshot(ArchiveQueue queue, FanControlTrace trace,
        SensorFrame? latest)
    {
        var export = TelemetryExportRuntime.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var lastCursor = Interlocked.Read(ref _lastExportCursorMs);
        double? exportLag = export.State != "disabled" && lastCursor >= 0 &&
                            latest?.Error == null && latest != null ?
            Math.Max(0, latest.Timestamp.ToUnixTimeMilliseconds() - lastCursor) / 1000.0 : null;
        double? deliveryAge = export.LastDeliveredUtc is { } delivered ?
            Math.Max(0, (now - delivered).TotalSeconds) : null;
        var coolingAge = Interlocked.Read(ref _oldestCoolingMs);
        return new("since-process-start", now,
            _scan.Snapshot(), _control.Snapshot(), _total.Snapshot(), _interval.Snapshot(),
            _families.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot(), StringComparer.Ordinal),
            Interlocked.Read(ref _delayed),
            new(Volatile.Read(ref _configuredCooling), Volatile.Read(ref _missingCooling),
                Volatile.Read(ref _staleCooling), coolingAge < 0 ? null : coolingAge),
            queue.Depth, queue.Capacity, queue.Dropped,
            trace.Depth, trace.Capacity, trace.Dropped,
            exportLag, export.State, deliveryAge);
    }

    // Reuses the already-whitelisted sensordash_reading wire protocol. These
    // virtual Pipeline series are NOT hardware sensors and do not enter the
    // collector frame, approved fan curves or their freshness guards.
    internal static string SerializeForPi(PipelineHealthSnapshot snapshot, string host, long timestampMs)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["pipeline:archive_queue_depth"] = snapshot.ArchiveQueueDepth,
            ["pipeline:archive_dropped_total"] = snapshot.ArchiveDropped,
            ["pipeline:trace_queue_depth"] = snapshot.FanTraceQueueDepth,
            ["pipeline:trace_dropped_total"] = snapshot.FanTraceDropped,
            ["pipeline:delayed_cycles_total"] = snapshot.DelayedControlCycles,
            ["pipeline:cooling_inputs_missing"] = snapshot.CoolingInputs.MissingSources,
            ["pipeline:cooling_inputs_stale"] = snapshot.CoolingInputs.StaleSources
        };
        void Latencies(string prefix, LatencySummary summary)
        {
            if (summary.Samples == 0) return;
            if (summary.MeanMs is double mean) values[$"pipeline:{prefix}_mean_ms"] = mean;
            if (summary.P95Ms is double p95) values[$"pipeline:{prefix}_p95_ms"] = p95;
            if (summary.P99Ms is double p99) values[$"pipeline:{prefix}_p99_ms"] = p99;
            if (summary.MaxMs is double max) values[$"pipeline:{prefix}_max_ms"] = max;
        }
        Latencies("scan", snapshot.HardwareScanMs);
        Latencies("control", snapshot.ControlMs);
        Latencies("cycle", snapshot.TotalCycleMs);
        Latencies("interval", snapshot.IntervalMs);
        if (snapshot.CoolingInputs.OldestObservedAgeMs is double age)
            values["pipeline:cooling_input_max_age_ms"] = age;
        if (snapshot.SampleExportCursorLagSeconds is double lag)
            values["pipeline:sample_export_cursor_lag_s"] = lag;
        if (snapshot.SecondsSinceLastDelivery is double delivery)
            values["pipeline:seconds_since_last_delivery"] = delivery;
        var rows = values.Where(kv => double.IsFinite(kv.Value) && kv.Value >= 0)
            .Select(kv => new TelemetryRow(kv.Key, timestampMs, kv.Value)).ToArray();
        var types = rows.ToDictionary(row => row.SensorId, _ => "Pipeline", StringComparer.Ordinal);
        return TelemetryLineProtocol.Serialize(rows, host, types);
    }
}
