# Collector hardware owner and asynchronous consumers

SensorDash deliberately keeps a **single owner of the LibreHardwareMonitor
Computer and PWM controls**. A second thread may make control decisions from
snapshots, but must not independently touch the same Super I/O / LHM objects.

## Critical path

The SensorWorker owns hardware family discovery, sleep/resume reinitialization,
hardware.Update(), frame construction, fan-curve evaluation, thermal guards and
PWM command application. This path is serialized so that a hardware read,
firmware reset and PWM write cannot race one another.

It publishes the immutable latest frame (one volatile reference update), then
offers optional history/event work to an independent bounded queue. Neither
operation performs SQL or serializes the browser's JSON.

## Observers

- **HistoryArchiveWorker** writes saved sensor samples and accessory-power
  estimates into SQLite and evaluates/stores non-safety threshold transitions.
  The worker also archives cooling transition events. It retries failed
  SQLite stages without blocking SensorWorker or repeating already committed
  stage writes. Stage-specific flags prevent duplicate cooling events.
- **LiveFrameBroadcastWorker** serializes sensor frames as JSON and fans out
  updates to browser subscribers. Its one-frame buffer deliberately coalesces
  superseded updates. The latest frame remains available immediately at
  `/api/live`.
- **TelemetryExportWorker** reads the durable SQLite retry spool and sends
  batches to the Pi. VictoriaMetrics retention is unchanged.
- **HistoryRetentionWorker** already performs incremental SQLite cleanup
  separately from scanning and fan control.

## Backpressure and durability

The archive handoff has **512 bounded work slots**. At the typical 10-second
recording interval, this is about 85 minutes of queued history under a total
database stall, although cooling transitions use additional slots.

These in-memory frames are **not yet durable**. Service crashes can lose
queued-but-uncommitted readings. When the queue reaches capacity,
`ArchiveQueue.Dropped` increments and the archival worker emits an operational
warning (Event ID 5104) about possible holes in Windows/Pi history. This is an
explicit tradeoff: storage failure cannot compromise physical fan control.
Do not treat the presence of a recent exported sample as proof that no earlier
in-memory frame was dropped.

Archive write failures produce bounded warning/recovery events (5101-5103).
The worker holds its current work item across retries and can catch up after
transient SQLite contention. It does not retry PWM commands.

## Buffered fan diagnostics

`FanControlTrace.Event` now only formats and enqueues an in-memory line.
A distinct `FanTraceWriterWorker` owns file append and 2 MiB rotation, with a
bounded 2,048-line queue and a best-effort shutdown flush. A long-lived disk
failure cannot delay a physical PWM command. If diagnostics exceed buffer
capacity or the file fails, operational events 5201 and 5202 report dropped
lines. A process crash can lose the buffered lines; these are diagnostics,
not a durable safety audit.

Windows operational Event Log notifications for thermal guard transitions
are still synchronous when emitted from the collector; they're infrequent
state changes, not the 1-second PWM trace. If their duration becomes
measurable in critical-path profiling, they can be queued separately.

A later instrumentation pass can examine scan vs PWM-evaluation latency and
whether cooling-critical hardware families warrant different *schedules*,
not separate LHM hardware-writing threads.

Any queue or thread split must preserve these safety invariants:
1. Only the hardware owner accesses LHM controls.
2. The current scan's sensor age and freshness guards determine PWM duty.
3. A slow or failed SQLite write cannot trigger `Computer.Close()` or prevent
   the next fan-control evaluation.
4. Browser subscribers may skip stale visual frames; archived history has
   explicit bounded backpressure and visible loss reporting.
