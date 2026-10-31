# ADR 0008: Separate Windows hardware control from Pi observability

- Date: 2026-10-31
- Status: Implemented on feature branch; physical Windows validation pending

## Authoritative Windows owner

SensorDashCollector alone samples hardware, assigns stable sensor IDs, and owns fan curves, fan-output approval, cooling systems and sensor/component association, accessory calibration, aliases, thresholds, recording policy, and tray readouts. The pump remains tachometer-only. The Pi cannot mutate any hardware or config.

The local browser now only offers Cooling Systems, Fan Control, Tray Readouts, Sensor Explorer, Events and Settings. Removed: desktop Overview, custom Dashboards, Virtual Sensors (including the evaluator and model), and Storage. Historical charts no longer duplicate Grafana in Cooling Systems. Virtual sensors are unused by the installed configuration and need no compatibility path.

Settings retains physical collection and storage-poll intervals, recording/retention controls for the local spool, aliases, thresholds, hardware/device enablement, and diagnostics. A same-origin HTTP + local pipe flow allows editing the outbound telemetry destination, host label, token and public CA. Raw credentials are never returned; no browser request can restart a service or activate PWM.

## Pi observability owner

The existing SQLite sample archive remains the bounded durable retry queue for TLS export. Removing it would lose delayed offline samples and durable cursor replay. VictoriaMetrics/Grafana own persistent history queries, disk/SSD monitoring, storage-buffer size trends, telemetry event timelines, retention, alerting and charting.

Export schema adds canonical storage device type labels, local database/WAL/SHM byte-size gauges, and timestamped threshold warning/recovery event impulses. Full event messages remain local and no free-form message labels or remote control commands are accepted. Current aliases and component relationships are metadata gauges; raw sensor IDs are immutable history keys.

## Rollout and safety

First deploy the Pi ingestion allowlist and new Grafana dashboards; its input accepts both older and new catalog metadata. Next use the Windows CI-produced MSI during a supervised hardware window. Verify cooling associations, radiator control and RPM feedback, pump tach-only semantics, NVMe fan control, tray icons, alert events, export and Windows sleep/resume. Back up the ProgramData folder and keep a rollback plan. Do not treat CI tests as proof of physical fan behavior.

No duplicate sensor hardware polling, remote control channel, fan-curve editing in Grafana, automatic service restart from the browser or fabricated energy through missing samples is allowed.
