# Opt-in export to HomeAwayFromHome Grafana

**Status:** first milestone / raw-sensor proof of concept. Existing SensorDash hardware collection, approved fan profiles, PWM fallback, cooling configurations, tray icons, SQLite archive and web dashboard are unchanged.

## Ownership boundary

- **Windows SensorDashCollector:** single authority for LHM sampling, sensor IDs, fan-control decisions, profile approval and cooling system definitions. Never use Grafana to write hardware settings.
- **Windows SQLite archive:** still records samples using existing retention/collection policy. A separate hosted worker reads this archive in small batches; no duplicate hardware polls or second persistent database.
- **HomeAway Pi:** authenticated TLS-only intake -> internal VictoriaMetrics -> Grafana dashboards. Pi failure must not impact fan control.
- **Current Windows local dashboard:** still available for configuration and side-by-side comparisons. Do not remove it before metrics and analysis parity tests.

## Enable explicitly on a test collector

The exporter is completely disabled unless the Windows Service can read a config file with `enabled: true`. No network listener, firewall change, reverse proxy, system certificate trust change or automatic service migration is needed.

Follow the Pi's [telemetry setup](https://github.com/CodyMathis123/HomeAwayFromHome/blob/feature/pi4-home-infrastructure/docs/TELEMETRY.md) to install the optional Grafana/VM profile, generate credentials and obtain a copy of the dedicated `ca.crt` and token securely.

Under `C:\ProgramData\SensorDash\`, with ACLs restricting access to SYSTEM and Administrators, create:

```json
{
  "enabled": true,
  "endpoint": "https://192.168.1.10:8429/ingest",
  "tokenFile": "C:\\ProgramData\\SensorDash\\telemetry-token.txt",
  "caCertificateFile": "C:\\ProgramData\\SensorDash\\telemetry-ca.crt",
  "hostLabel": "main-desktop"
}
```

Save this as `telemetry-export.json`. Copy the Pi-issued CA certificate into `telemetry-ca.crt` and the **secret** bearer token into `telemetry-token.txt`. Replace the example IP with the reserved Pi IPv4; the HTTPS certificate has this IP as its SAN. Restart the **SensorDashCollector** service deliberately after configuration. No UI toggle or auto-reload exists yet.

Never commit or share `telemetry-token.txt`, and never turn off certificate-name or CA validation. Do not copy the Pi's CA private key to Windows.

## What exports

- Only archived numeric samples, in order by timestamp and stable sensor ID.
- First activation begins at the latest existing sample, avoiding a surprise historical backfill. Importing older local history will be a separate, reviewed migration.
- A 384-sample max request, preserving each archived millisecond timestamp. Batch requests are separated by brief delays; network errors have retries.
- Each series has the Prometheus-compatible name `sensordash_reading_value`, with labels `host`, `sensor` and `kind`.
- Retains a resume cursor in `telemetry-export-cursor.json`. A successful HTTP response is required before advancing. A crash after server acceptance but before cursor persistence can replay a batch; the Pi's VictoriaMetrics profile enables 1 ms query deduplication.
- The existing SQLite history and retention act as the bounded queue. If the Pi is unreachable longer than Windows retention, data may be lost when old samples expire. Failure is logged without affecting collector fan operation.

Sensor aliases are intentionally **not** included as series labels because alias edits would fragment historical identity. Sensor catalog/alias metadata, cooling components and derived wattage estimates are the next milestone.

## Diagnosis

1. Verify `SensorDashCollector` is running and has recent readings in the existing dashboard.
2. Confirm the Pi's restricted intake container is running and can reach `victoria-metrics` internally.
3. Check the Windows Service event logs / collector logging for `Telemetry export` messages.
4. In Grafana, inspect **HomeAway → SensorDash · Hardware Telemetry**. Select the host and sensor IDs. The dashboards will remain empty until new samples arrive and the exporter is enabled.
5. If a token or CA is rotated, update both sides consistently and restart the collector. Do not delete the cursor casually; that may replay a large history or prevent expected delivery.

### Safety and migration criteria

Before retiring Windows history graphs, compare time-aligned readings, raw recorded maxima, device sleep gaps, energy coverage and estimates across both systems. New Pi metrics are observational only; virtual sensors used for fan control, approved profiles, cooling topology and electrical configuration remain local.

The full plan is in [ADR 0007](adr/0007-remote-telemetry-boundary.md).
