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

**Milestone 2 update:** To avoid splitting raw history when a user renames a sensor, a separate `sensordash_sensor_info` gauge publishes a bounded stable-ID catalog, and `sensordash_cooling_binding` gauges publish current system/component relationships. Each metadata series is serialized as a strict set of tags with a 0/1 value. On edits, removed label sets receive explicit 0 tombstones; the previous acknowledged set is persisted to disk, and metadata is refreshed every two minutes. The Pi's gateway accepts only these two new exact schemas and the original raw reading schema, never arbitrary measurements or commands. Grafana joins readings against bindings by `on(host,sensor) group_right(...)` rather than copying sensor history per component. This is observational metadata only.

**Milestone 3:** The Windows collector now archives fan/pump watts at each existing history interval in a **separate table inside the existing SQLite database**, using the same C# `AccessoryPowerModel.Estimate` function as the prior historical statistics. The archive persists watts, measured/calibrated/curve/rated provenance and a hash of the electrical calibration/sensor configuration *as it existed when the sample was observed*. This avoids retroactively changing history after a calibration edit. A separate durable export cursor ships the derived values through the same authenticated outbound HTTPS channel, and independent read-only `sensordash_component_info` gauges provide human-readable labels. The core fan-control loop does not consult the network; failures recording accessory telemetry are caught and logged.

**Remaining next phase:** compare electrical output and gap-aware historical energy/coverage with the local UI against real measurements before publishing watt-hours or removing any local history views. JavaScript still handles some existing local presentation calculations during the side-by-side migration; do not delete these until parity is demonstrated.
