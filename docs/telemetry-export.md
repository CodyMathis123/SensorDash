# SensorDash outbound telemetry → HomeAway Grafana

**Architecture:** the local Windows collector remains responsible for hardware, while a separate bounded worker reads already-recorded SQLite rows and POSTs to the Pi over authenticated TLS. The Pi can display metrics but has **no** endpoint for fan commands, sensor aliases, cooling topology edits or local collection policies.

## Windows UI configuration

1. Deploy the Pi ingestion allowlist and dashboards from [HomeAwayFromHome](https://github.com/CodyMathis123/HomeAwayFromHome/pull/1) first. Set up Pi-issued token and public CA certificate using that repository's instructions.
2. On the Windows machine, open `http://127.0.0.1:8765` → **Settings → Pi telemetry export**. Enter the HTTPS endpoint (`https://192.168.1.43:8429/ingest` for this deployment), stable host label (`main-desktop`), and optionally the bearer token and public CA PEM if not already installed.
3. Enable export and save. Existing token and CA are **preserved when optional replacement fields are blank**. Secrets are stored under `%ProgramData%\SensorDash`, protected by restricted SYSTEM/Administrators NTFS permissions when newly written. The UI's read endpoint returns presence/status only, not the secret values.
4. The exporter automatically enables/reconnects after a change in its config/token/CA content. There is **no need to restart the privileged Windows service** to edit Pi export settings. SensorDash does not restart or modify fan outputs when the exporter reconnects.
5. In Settings, check exporter state, last successful delivery, last failure class and reload count. In Grafana, inspect `SensorDash · Overview`, `Cooling Systems`, `Fan & Pump Electricity`, `Storage`, and `Events`. No event impulse appears unless a local threshold actually crosses or recovers.

The export settings file `telemetry-export.json` is still an internal durable configuration file with `enabled`, `endpoint`, `hostLabel`, `tokenFile` and `caCertificateFile` fields. Using the UI is preferred to editing JSON manually. Neither the file nor its secrets are sent to the Pi.

## Series and data guarantees

The receiver's strict allowlist accepts only:
- Raw numeric sensor values, indexed by stable host/sensor IDs, with original sample times.
- Sensor metadata (name, alias, device and hardware type), cooling-system/component bindings, component catalog and metadata tombstones for removed assignments.
- Fan/pump accessory watts recorded **at collection time** with source `measured`, `calibrated`, `curve` or `rated`, and calibration revision. A change in the number of fans or power model creates a distinct revision; estimates are not retrospectively overwritten.
- Event warning/recovery impulses at their observed timestamps; full Windows event messages remain local.
- Aggregate byte sizes of the local SQLite database and WAL/SHM files. These are **not** per-sensor byte allocation estimates.

No private IP endpoint, domain-level DNS history, telemetry bearer, private CA key, arbitrary events, remote control messages, or arbitrary dynamic Influx tags are exported.

The worker persists separate cursors for raw samples, accessory watts and events. A successful receiver acknowledgement must occur before a cursor advances. Incomplete writes retry and a Pi outage never changes PWM. On first activation, archived numeric samples start from the newest retained sample rather than silently uploading the entire old database; events use their own bounded cursor.

**Historical limitations:** no watt-hours are inferred through Windows sleep/shutdown gaps. Estimated accessory watts are source/revision-labeled and must not be conflated with a physical wall-power sensor. Current sample counts aren't precise per-sensor database sizes because VictoriaMetrics storage is compressed/shared. Missing coolant or flow probes remain genuinely absent.

## Troubleshooting

- **Disabled:** confirm Settings toggle. The worker remains running and periodically checks configuration; it no longer exits when disabled.
- **Retrying:** check the last failure class and local Windows service logs. Confirm that the Pi receiver and TLS certificate for `192.168.1.43` are valid, and the Pi-issued token matches. Do not paste tokens, CA private keys or full credential files into chat.
- **No data in Grafana:** verify the Pi receiver and VictoriaMetrics containers, then run `sudo bash scripts/verify-telemetry.sh main-desktop` on the Pi. Raw and catalog series should be populated; the event series can legitimately be empty between threshold changes.
- **Still seeing old configuration:** the exporter re-fingerprints config/token/CA before each batch and constructs a fresh TLS/HTTP session on change. Wait for the next sender iteration or check its retry status; **do not restart a working fan-controlling service solely for telemetry settings**.
- **Windows upgrade:** back up `%ProgramData%\SensorDash` with an SQLite-consistent copy and preserve the existing approved fan profiles. Validate physical fan safety before any supervised service restart/install.

See [HomeAway telemetry guide](https://github.com/CodyMathis123/HomeAwayFromHome/blob/feature/pi4-home-infrastructure/docs/TELEMETRY.md), [ADR 0008](adr/0008-local-control-observability.md).
