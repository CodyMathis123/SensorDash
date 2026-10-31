# 0007: Opt-in, outbound-only historical telemetry export

- Date: 2026-10-31
- Status: Experimental / first migration milestone

## Context

SensorDash currently owns both an elevated Windows hardware/control service and a custom local history/chart frontend. A separate always-on Pi can provide Grafana visualization for multiple machines. Reimplementing generic charting and time-series query behavior inside SensorDash is undesirable, but moving safety-critical fan control and cooling topology to an external server would create dangerous dependency and authorization gaps.

## Decision

Add a **disabled-by-default** exporter hosted independently of the fast collection/control loop. It reads samples already committed to the existing SQLite archive (same local retention policy), starts from the newest recorded sample on its first activation, paginates with a persisted timestamp/sensor cursor, and ships original timestamps in a strict Influx-compatible schema. The exporter creates an opt-in SQLite index on `(ts,sensor)` before the SensorWorker starts, avoiding repeated full scans of the preexisting `(sensor,ts)` archive; the one-time index build may delay startup and require free disk. On successful server response the cursor advances atomically. Non-2xx responses retain the cursor and retry.

Export only to an explicit HTTPS endpoint with a private CA trusted *per exporter*, verify the hostname/IP, and use a strong bearer token. Pi deploys an isolated, write-only TLS receiver; VictoriaMetrics has no LAN-published port. Grafana reads time series but has no remote fan-control authority.

The collector remains the sole owner of sensor inventory, aliases, virtual readings used by control, logical cooling system configuration, power calibration, fan profile approval and physical outputs. The existing local browser UI and tray remain until analysis/dashboard parity has been demonstrated.

## Alternatives considered

- Re-poll hardware in a separate Telegraf agent: rejected because of duplicated hardware access and divergent sensor identifiers.
- Build a new local disk spool: deferred; existing persisted samples and retention can buffer a small telemetry pipeline without extra storage/locking.
- Make the Pi poll the privileged SensorDash API: rejected due to inbound Windows attack surface and fan-control trust boundaries.
- Replace the local UI immediately: rejected until dashboard parity and real hardware integration tests are done.
- Introduce Grafana-defined cooling configurations: rejected because it duplicates authoritative fan/pump/sensor mappings.

## Known limits

The MVP lacks explicit historical backfill, live alias/catalog propagation, cooling-system labels, derived-power telemetry and remote monitoring status in the local UI. Original names may be unavailable for older sensors. Existing SQLite retention can expire samples not yet delivered. A crash between successful ingest and cursor commit can replay a batch; queries should deduplicate repeated timestamps. No dynamic config reload or service installation is performed by the exporter itself. Hardware validation is separate from Windows CI.

Next phase is stable sensor catalog and cooling metadata, read-only derived metrics with source/revision labels, and comparison against current raw peak, energy coverage and correlation semantics.
