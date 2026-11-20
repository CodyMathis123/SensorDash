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

## Next stage

Disk-backed fan trace logging and event-log notifications still originate
from the cooling path. The next PR buffers that diagnostic I/O, without
changing the LHM single-owner rule. A later instrumentation pass can examine
hardware-scan versus PWM-evaluation latency and whether cooling-critical
sensor groups warrant independent *schedules*, not independent hardware writers.

Any queue or thread split must preserve these safety invariants:
1. Only the hardware owner accesses LHM controls.
2. The current scan's sensor age and freshness guards determine PWM duty.
3. A slow or failed SQLite write cannot trigger `Computer.Close()` or prevent
   the next fan-control evaluation.
4. Browser subscribers may skip stale visual frames; archived history has
   explicit bounded backpressure and visible loss reporting.
