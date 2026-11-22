# Collector performance and pipeline health

## Windows Event Viewer source names

The MSI installs a Windows service named `SensorDashCollector` and a dedicated
classic Event Log called `SensorDash` with source `SensorDash.Collector`.
These are intentionally different roles, not two application loggers.

Windows `ServiceBase.AutoLog` can independently write service lifecycle
information (Event ID 0) under the service name in the Application log.
The collector disables that **redundant** automatic service logging before
starting the Windows service lifetime. Operational lifecycle events 1000/1001
remain under `SensorDash.Collector`. Windows Service Control Manager crash
and restart events still appear under the System log. Historical Application
entries are not migrated or deleted.

## Local diagnostics

Read `http://localhost:8765/api/pipeline-health` for a self-contained,
read-only snapshot. The existing `/api/diagnostics` response also embeds a
`pipeline` field.

- `hardwareScanMs`, `controlMs`, `totalCycleMs`, and `intervalMs` provide
  count, mean, p95, p99 and exact observed maximum in milliseconds.
- `hardwareFamiliesMs` attributes scan duration to LHM hardware families
  (not unique device identifiers). No new hardware scanning is introduced.
- `coolingInputs` reports counts of approved live-curve/thermal-guard inputs
  that are missing or stale, plus the oldest observed input age.
- `delayedControlCycles` counts unusually long inter-cycle gaps, excluding
  intentional Windows resume gaps.
- `archiveQueueDepth`/`archiveDropped` and
  `fanTraceQueueDepth`/`fanTraceDropped` reveal backpressure and diagnostic
  loss. These queues are bounded and must not stall cooling.
- `sampleExportCursorLagSeconds` compares the latest collector frame time
  with the exporter's last acknowledged sensor cursor. It does not prove
  every reading was recorded or received. `secondsSinceLastDelivery`
  reports time since the exporter's last successful send.

Percentiles are **approximate, fixed-bucket and since process start**; they are
not rolling-hour percentiles or a precision latency profiler. `percentileMethod`
identifies the reporting method: `p95Ms` and `p99Ms` are upper-bucket estimates,
capped to the exact observed `maxMs`. This avoids showing p99 above the largest
actual measurement.

Windows 10+ provides `QueryUnbiasedInterruptTimePrecise`, a high-resolution
awake-only clock that excludes time spent sleeping or hibernating. Every
critical-stage duration compares that clock to the process Stopwatch
(which includes suspended time). Only a **large, corroborated divergence**
(at least 2 seconds and 20% of the measured stage) classifies a scan as
`sleepInterrupted`. These observations are counted separately, with
`lastSleepInterruptedUtc` and `lastExcludedSleepMs`, and are excluded from
normal averages/percentiles/maxima; the hardware scan is **not retried or
reordered** for instrumentation. Without the native clock, no sleep
classification is invented. Truly slow awake scans advance both clocks
and remain fully included.

Each stage and hardware family also exposes `over250Ms`, `over500Ms`,
`over1000Ms`, and `over2000Ms` for genuinely slow **awake** observations.
`windowsResumeNotifications` and `lastWindowsResumeUtc` help correlate
an interrupted scan with actual Windows resume notifications; they are not
assumed to arrive during the scan itself. This is a measurement correction,
not a change to fan/rearm logic.

Values are read from in-memory counters. They do not require SQLite queries
or force a GC.

The existing opt-in process performance sampler remains separate and
unchanged; this adds only a few Stopwatch timestamps and atomic counters per
hardware/control loop. `controlMs` includes curve/guard evaluation and PWM
application, not hardware writes alone.

## Pi / VictoriaMetrics

The exporter attaches a few `kind=Pipeline` gauges to its existing
once-per-minute spool-health request. These use the
`sensordash_reading,host=...,sensor=pipeline:...,kind=Pipeline value=... ts`
schema already accepted by HomeAwayFromHome's ingest allowlist. No backend
upgrade, token change, or new endpoint is required.

Examples:

- `pipeline:scan_sleep_interrupted_total` and `pipeline:windows_resume_signals_total`
- `pipeline:scan_slow_over_500ms_total` and `pipeline:scan_slow_over_1000ms_total`
- `pipeline:scan_p99_ms`
- `pipeline:control_p99_ms`
- `pipeline:cycle_max_ms`
- `pipeline:archive_queue_depth`
- `pipeline:archive_dropped_total`
- `pipeline:trace_queue_depth`
- `pipeline:sample_export_cursor_lag_s`

These are synthetic reporting gauges, **not hardware sensor identities** and
are never added to a fan curve's available inputs. Like existing spool-size
gauges, they are best-effort observations, not durable per-sample history.

## Next validation

1. Update the Windows MSI.
2. Use the collector for a normal gaming session and one sleep/resume cycle.
3. Compare the local latency p95/p99/max with export lag and dropped-frame
   counters. A consistently high scan time or one slow hardware family is a
   stronger reason to adjust polling schedules than launching a competing
   thread to write motherboard PWM registers.
4. Confirm Event Viewer shows `SensorDash.Collector` lifecycle events and
   no new normal `SensorDashCollector` Event ID 0 entries. Historical events
   remain visible.
