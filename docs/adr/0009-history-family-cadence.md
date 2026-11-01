# 0009: Independent local-history cadence by hardware family

**Status:** Accepted (implementation in `feature/history-family-cadence`)

## Context

SensorDash has a fast LibreHardwareMonitor sampling/control loop, a SQLite
history/retry spool and a cursor-based TLS exporter to the Pi's VictoriaMetrics.
Historically every eligible sensor was archived at `SaveEverySeconds`, typically
every 10 seconds. Enabling Storage and Network collection can introduce hundreds
of device/counter readings that provide useful long-term trends but do not need
the same temporal resolution as CPU, GPU, coolant, fans and pump sensors.
VictoriaMetrics compresses this well, so data volume alone does not justify
reducing the Pi's retention.

The existing `StoragePollSeconds` option controls hardware refresh, not history:
cached Storage readings were eligible for repeated SQLite inserts between actual
drive polls. Changing global `SaveEverySeconds` would also sacrifice thermal
granularity and affect the local archive's durable export stream.

## Decision

Implement **independent history intervals** for hardware types `Storage` and
`Network`, both defaulting to 60 seconds (configurable from 5 to 3600 seconds)
in the SensorDash Settings UI. Other sensors retain the existing global
`SaveEverySeconds` (default 10 seconds). A configured global save interval
is a floor: these slower categories cannot schedule additional database writes
between global archives. The collector's live poll loop, PWM, approved profiles,
LHM hardware collection switches and configurable local retention do not change.

Sensors explicitly referenced in cooling systems, power measurements, tray,
thresholds, simulated curves, fan profiles or pins always retain the faster
global archive interval, regardless of hardware category. Explicitly excluded
hardware types still remain excluded; history mode selection (`balanced`,
`in-use`, `all`) remains authoritative. Device visibility is not collection
or archive policy.

Throttle **at the SQLite `HistoryStore.Record` write boundary**. A monotonic
per-sensor last-successfully-archived timestamp determines whether each sample is
due. Storage samples with an unchanged `ObservedAt` from LHM are not archived
again even if the global SQLite interval has elapsed. Advance archive markers
only after the SQLite transaction commits. Stored samples retain the original
frame timestamp. Existing SQLite tables, Pi metric schema, exporter cursors,
retry/replay semantics and VictoriaMetrics 90-day retention remain unchanged.
The timestamps are never backfilled or relabeled to fake higher-resolution
measurements.

## Tradeoffs and limitations

- The new family intervals apply to **future** recordings; existing Pi data is
  untouched, and no historical downsampling or backfill migration runs.
- Per-family settings control the SQLite archive and thus downstream export;
  they do not alter live Sensor Explorer, PWM supervision or Windows events.
- Individual NIC/SMART sensor exclusions can still use the existing in-use or
  hardware-type exclusion policies. More granular per-sensor overrides or
  rollup tables are deferred until there is real disk-growth evidence.
- The first eligible sample after a service restart can be written immediately
  even if the prior process had recently written that sensor. That is at most
  one extra entry per sensor per restart, not a duplicate series or modified
  historical record.
- Longer intervals have lower peak-fidelity for the affected categories;
  pinned/important channels keep the high-resolution schedule.
- Pi long-term aggregates can be added later without modifying the reliable
  retry spool or the fan controller.

## Validation

Hardware-free tests write actual SQLite samples and assert retained 10-second
thermal history, 60-second Storage/Network history, no cached drive duplicates,
correct pinned-sensor priority and unchanged timestamp/export schema. Settings
tests ensure family intervals survive UI updates and the service IPC path.
The existing Windows MSI build, prior-Production upgrade and fan-resume tests
remain mandatory before merge or installation.
