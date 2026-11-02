# SensorDash outbound telemetry → HomeAway Grafana

## Canonical HomeAway HTTPS endpoint

For HomeAway deployments with the canonical metrics gateway route enabled,
use **https://metrics.homeawayfromhome.house/ingest** from Windows SensorDash.

1. First deploy and verify the gateway route on the Pi using the companion
   HomeAwayFromHome telemetry proxy change. The Pi's old :8429 port remains
   available during migration; do not switch Windows before the proxy returns
   HTTP 401 to an anonymous POST and Grafana login still works.
2. In SensorDash -> Settings -> Pi telemetry export, change the endpoint to
   https://metrics.homeawayfromhome.house/ingest and check
   **Use Windows certificate trust**.
3. Leave the optional token/CA replacement fields blank to retain the existing
   bearer and Pi private CA files, then Save. The exporter hot-reloads, without
   restarting fan control.
4. Confirm a recent last-delivered timestamp in SensorDash and new data in
   Grafana. The Windows system must resolve the private metrics hostname to
   the Pi via LAN/VPN DNS, and must trust its valid Let's Encrypt certificate.

The Windows trust option is intentionally permitted **only** for that exact
HTTPS metrics host and the default port 443. It uses the Windows certificate
chain and hostname checks rather than disabling TLS validation.

To roll back, restore the previously configured
https://PI_LAN_IP:8429/ingest URL and **uncheck** Windows certificate trust.
The old receiver and private CA remain installed during this staged cutover.


**Architecture:** the local Windows collector remains responsible for hardware, while a separate bounded worker reads already-recorded SQLite rows and POSTs to the Pi over authenticated TLS. The Pi can display metrics but has **no** endpoint for fan commands, sensor aliases, cooling topology edits or local collection policies.

## Windows UI configuration

1. Deploy the matching Pi ingestion allowlist and dashboards first, following the [HomeAway telemetry guide](https://github.com/CodyMathis123/HomeAwayFromHome/blob/feature/homeaway-auth-pc-control/docs/TELEMETRY.md) (in the stacked HomeAway PRs). Set up the Pi-issued token and public CA certificate using that repository's instructions. Do not infer from an unmerged feature branch that the deployed Pi already accepts the newer schema.
2. On the Windows machine, open `http://127.0.0.1:8765` → **Settings → Pi telemetry export**. Enter the HTTPS endpoint `https://PI_LAN_IP:8429/ingest` with the Pi's actual reserved IPv4 address (the certificate must match it), a stable host label such as `main-desktop`, and optionally the bearer token and public CA PEM if not already installed.
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

Storage and Network now have separate local historical recording intervals (60 seconds by default), configured under SensorDash Settings. The 10-second important-sensor history continues for CPU/GPU/cooling and any pinned or referenced sensor, even if it belongs to Storage or Network. The Storage hardware polling interval is independent. Cached, unchanged storage observations do not create additional archived samples, and the Pi receives only committed SQLite rows. This changes future history volume, not existing Pi retention or fan-control polling.

The worker persists separate cursors for raw samples, accessory watts and events. A successful receiver acknowledgement must occur before a cursor advances. Incomplete writes retry and a Pi outage never changes PWM. On first activation, archived numeric samples start from the newest retained sample rather than silently uploading the entire old database; events use their own bounded cursor.

**Historical limitations:** no watt-hours are inferred through Windows sleep/shutdown gaps. Estimated accessory watts are source/revision-labeled and must not be conflated with a physical wall-power sensor. Current sample counts aren't precise per-sensor database sizes because VictoriaMetrics storage is compressed/shared. Missing coolant or flow probes remain genuinely absent.

## Troubleshooting

- **Disabled:** confirm Settings toggle. The worker remains running and periodically checks configuration; it no longer exits when disabled.
- **Retrying:** check the last failure class and local Windows service logs. Confirm that the Pi receiver and TLS certificate for your configured Pi IPv4 address are valid, and the Pi-issued token matches. Do not paste tokens, CA private keys or full credential files into chat.
- **No data in Grafana:** verify the Pi receiver and VictoriaMetrics containers, then run `sudo bash scripts/verify-telemetry.sh main-desktop` on the Pi. Raw and catalog series should be populated; the event series can legitimately be empty between threshold changes.
- **Still seeing old configuration:** the exporter re-fingerprints config/token/CA before each batch and constructs a fresh TLS/HTTP session on change. Wait for the next sender iteration or check its retry status; **do not restart a working fan-controlling service solely for telemetry settings**.
- **Windows upgrade:** back up `%ProgramData%\SensorDash` with an SQLite-consistent copy and preserve the existing approved fan profiles. Validate physical fan safety before any supervised service restart/install.

### Quick sleep/wake fan recovery validation (Windows integration gate)

A real hardware regression was observed on the 0.8.324 MSI: after a **short sleep and immediate wake**, approved profiles remained listed but physical PWM control was not reapplied until `SensorDashCollector` restarted. Long sleeps often worked. The old recovery only treated a collector polling gap **greater than 20 seconds** as a resume; quick sleep/wake can be shorter.

The follow-up build registers the **elevated collector** with the Windows suspend/resume notification API. On `PBT_APMRESUMEAUTOMATIC` it queues a one-shot recovery; the hardware polling thread closes and reopens the LibreHardwareMonitor `Computer` and reapplies **already approved** persistent profiles. The longer-than-20-second gap mechanism remains a fallback if native events are unavailable. The callback does not write PWM, approve profiles, use the Pi, or start new manual commissioning sessions.

For manual validation after installing the new CI MSI:
1. Confirm motherboard firmware fan fallback is safe, service is running, and actual radiator/NVMe RPM matches the active curve. The DDC remains tach-only.
2. Sleep briefly and resume promptly, then compare requested duty and **measured RPM** (not just the stored profile state). Repeat for a much longer sleep.
3. Open **Fan Control → Show recent log** or inspect the local protected `%ProgramData%\SensorDash\logs\fan-control.log` for `[HW-RESET]` followed by `[HW-REOPEN] reason=windows-resume` (a native wake signal) or `reason=polling-gap` (the long-gap fallback), indicating the collection thread reinitialized LHM without stopping the service. Check normal one-second polling does *not* repeatedly reopen.
4. Compare with `SensorDashCollector` Windows service logs: registration should report native resume notifications active, or a warning that gap-only fallback remains. If short wake still fails, capture the before/after fan log and hardware status before restarting the service.
5. Keep this as a **live Windows hardware acceptance gate**; synthetic policy tests and MSI build success cannot prove the motherboard's real PWM resumed.


See [HomeAway telemetry guide](https://github.com/CodyMathis123/HomeAwayFromHome/blob/feature/homeaway-auth-pc-control/docs/TELEMETRY.md), [ADR 0008](adr/0008-local-control-observability.md).

### CI validation cadence

Windows MSI/fan-control validation is required after code, configuration,
test or workflow changes. Markdown-only documentation commits do not rebuild
the MSI and can be reviewed without waiting for Windows CI. A fresh manual
workflow run remains available if documentation changes alter deployment
instructions that merit an end-to-end recheck.
