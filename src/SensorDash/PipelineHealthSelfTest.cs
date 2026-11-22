using System.Text.RegularExpressions;

// Pure synthetic checks: no sensors opened, no PWM writes, no HTTP socket,
// no Windows Event Log and no persistent database operations.
internal static class PipelineHealthSelfTest
{
    internal static void Run()
    {
        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("Pipeline health: " + message);
        }

        var histogram = new LatencyHistogram();
        for (var i = 0; i < 95; i++) histogram.Observe(1);
        for (var i = 0; i < 4; i++) histogram.Observe(20);
        histogram.Observe(300);
        histogram.Observe(double.NaN);
        histogram.Observe(-1);
        var summary = histogram.Snapshot();
        Check(summary.Samples == 100 && summary.P95Ms == 1 &&
              summary.P99Ms == 20 && summary.MaxMs == 300,
              "bounded histogram quantiles, maximum, and invalid values");

        // Reproduce the actual 23h58m suspended GPU scan without relying on
        // a Windows sleep event in CI. The awake clock has only advanced 40ms.
        var suspended = StageDuration.Classify(86_318_789.689, 40);
        Check(suspended.SleepInterrupted &&
              suspended.ExcludedSleepMs is > 86_318_000,
              "almost-day-long GPU measurement must be classified as suspension");

        // Real hardware driver stalls advance both clocks, even when huge.
        var trueStall = StageDuration.Classify(1558.696, 1558.4);
        Check(!trueStall.SleepInterrupted && trueStall.ExcludedSleepMs == null,
              "genuine awake hardware stalls must remain visible");
        Check(!StageDuration.Classify(2500, null).SleepInterrupted,
              "unavailable native clock must never invent a sleep diagnosis");
        Check(!StageDuration.Classify(1000, 250).SleepInterrupted,
              "small disagreements must not hide real scan delays");

        var scan = new LatencyHistogram();
        for (var i = 0; i < 32; i++) scan.Observe(.2);
        scan.Observe(788.855);
        scan.Observe(trueStall);
        scan.Observe(2100);
        scan.Observe(suspended);
        var corrected = scan.Snapshot();
        Check(corrected.Samples == 35 && corrected.SleepInterrupted == 1 &&
              corrected.P99Ms <= corrected.MaxMs &&
              corrected.P99Ms == corrected.MaxMs &&
              corrected.MeanMs is < 200 &&
              corrected.LastExcludedSleepMs > 86_318_000 &&
              corrected.LastSleepInterruptedUtc.HasValue,
              "sleep outlier must be separate from scan mean, percentile and max");
        Check(corrected.Over250Ms == 3 && corrected.Over500Ms == 3 &&
              corrected.Over1000Ms == 2 && corrected.Over2000Ms == 1,
              "awake slow scans must be counted at 250/500/1000/2000ms thresholds");
        Check(corrected.PercentileMethod == "bucket-upper-bound-capped-to-max",
              "approximate percentiles must identify their method");

        var folder = Directory.CreateTempSubdirectory("SensorDash-pipeline-");
        try
        {
            var metrics = new PipelineHealthMetrics();
            var queue = new ArchiveQueue(2);
            var trace = new FanControlTrace(folder.FullName, 2);
            var now = DateTimeOffset.UtcNow;
            var readings = new List<SensorReading>
            {
                new("fresh", "Fresh CPU", "CPU", "Cpu", "Temperature",
                    40, now.AddSeconds(-1)),
                new("old", "Old GPU", "GPU", "Gpu", "Temperature",
                    55, now.AddSeconds(-12))
            };
            var frame = new SensorFrame(now, readings, null);
            metrics.ObserveScan(4);
            metrics.ObserveScan(6);
            metrics.ObserveControl(.5);
            metrics.ObserveCycle(8);
            metrics.ObserveInterval(TimeSpan.FromMilliseconds(2700), 1, false);
            metrics.ObserveInterval(TimeSpan.FromHours(8), 1, true);
            metrics.ObserveFamily("Cpu", 2);
            metrics.ObserveFamily("Gpu", 6);
            metrics.ObserveFamily("Gpu", suspended);
            metrics.ObserveFamily("Cpu", trueStall);
            metrics.ObserveWindowsResumeSignal(now);
            metrics.ObserveCoolingInputs(frame,
                [
                    new CoolingInputRequirement("fresh", 5),
                    new CoolingInputRequirement("old", 5),
                    new CoolingInputRequirement("not-available", 5)
                ], now);
            var settings = new DashboardSettings();
            Check(queue.TryEnqueue(frame, settings, true, []), "queue first");
            Check(queue.TryEnqueue(frame, settings, true, []), "queue second");
            Check(!queue.TryEnqueue(frame, settings, true, []), "queue bounded");
            trace.Event("TEST", "first");
            trace.Event("TEST", "second");
            trace.Event("TEST", "overflow");
            TelemetryExportRuntime.Running();
            metrics.SetExportCursor(now.AddSeconds(-5).ToUnixTimeMilliseconds());
            var health = metrics.Snapshot(queue, trace, frame);
            Check(health.HardwareScanMs.Samples == 2 &&
                  health.ControlMs.Samples == 1 &&
                  health.TotalCycleMs.Samples == 1 &&
                  health.IntervalMs.Samples == 1 &&
                  health.DelayedControlCycles == 1,
                  "stage counts and resume gaps");
            Check(health.HardwareFamiliesMs.Count == 2, "bounded family diagnostics");
            Check(health.HardwareFamiliesMs["Gpu"].Samples == 1 &&
                  health.HardwareFamiliesMs["Gpu"].SleepInterrupted == 1 &&
                  health.HardwareFamiliesMs["Cpu"].Over1000Ms == 1 &&
                  health.WindowsResumeNotifications == 1 &&
                  health.LastWindowsResumeUtc == now,
                  "per-family suspended scan separate from real awake stalls and resume signal");
            Check(health.CoolingInputs.ConfiguredSources == 3 &&
                  health.CoolingInputs.MissingSources == 1 &&
                  health.CoolingInputs.StaleSources == 1 &&
                  health.CoolingInputs.OldestObservedAgeMs >= 12000,
                  "only approved cooling inputs determine freshness");
            Check(health.ArchiveQueueCapacity == 2 &&
                  health.ArchiveQueueDepth == 2 &&
                  health.ArchiveDropped == 1 &&
                  health.FanTraceQueueDepth == 2 &&
                  health.FanTraceDropped == 1,
                  "queue pressure and dropped counts");
            Check(health.SampleExportCursorLagSeconds is >= 4 and <= 6,
                  "export cursor lag measures readings versus checkpoint");

            var wire = PipelineHealthMetrics.SerializeForPi(
                health, "my-pc", now.ToUnixTimeMilliseconds());
            var lines = wire.TrimEnd('\n').Split('\n');
            Check(lines.Length >= 15 && lines.Length < 100,
                  "bounded one-minute health payload");
            var validator = new Regex(
                @"^sensordash_reading,host=my-pc,sensor=pipeline:[A-Za-z0-9_]+,kind=Pipeline value=[0-9.eE+]+ [0-9]{13}$",
                RegexOptions.CultureInvariant);
            Check(lines.All(line => validator.IsMatch(line)) &&
                  !wire.Contains("NaN") && !wire.Contains("Infinity") &&
                  !wire.Contains("token", StringComparison.OrdinalIgnoreCase),
                  "Pi allowlisted line protocol, finite values and no secrets");
            Check(wire.Contains("pipeline:control_p99_ms") &&
                  wire.Contains("pipeline:sample_export_cursor_lag_s"),
                  "actionable timing and exporter metrics are transmitted");
            metrics.SetExportCursor(-1);
            TelemetryExportRuntime.Disabled();
            Check(metrics.Snapshot(queue, trace, frame).SampleExportCursorLagSeconds == null,
                  "disabled exporter must not report a stale cursor");
            Check(new LatencyHistogram().Snapshot().Samples == 0,
                  "empty histogram is reported honestly");
        }
        finally
        {
            TelemetryExportRuntime.Disabled();
            try { folder.Delete(true); } catch (IOException) { }
        }
    }
}
