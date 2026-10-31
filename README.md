# SensorDash — Windows hardware control companion

SensorDash is a Windows hardware-monitoring and control utility for [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor). Its elevated collector is the **single authority** for sensor polling, local cooling topology, approved fan curves and physical PWM outputs. A small tray process and loopback web interface expose local controls. The Raspberry Pi hosts [HomeAwayFromHome](https://github.com/CodyMathis123/HomeAwayFromHome), Grafana and VictoriaMetrics for read-only history, infrastructure health and alert analysis.

## Ownership boundary

| SensorDash on Windows | HomeAway / Grafana on Pi |
|---|---|
| Fan curves, profile approval and firmware fallback | Historical sensor measurements and recorded maxima |
| Cooling-system names, roles, associations and power-model calibration | Cooling trends and estimated fan/pump electricity |
| Sensor discovery, aliases, thresholds and recording policies | Sensor metadata, historical event impulses and archive statistics |
| Tray readings, live cooling status and local alert messages | Storage-device temperature/activity trends and monitoring |
| Outbound TLS exporter URL/host/credentials | Pi storage usage, scrape status, data retention and dashboards |

The Windows interface has **Cooling Systems, Fan Control, Tray Readouts, Sensor Explorer, Events and Settings**. The old Windows Overview, custom Dashboards, Virtual Sensors and Storage/history chart pages were deliberately removed. We no longer ship the client history-chart engine or its associated static web assets. Long-term visualization is owned by Grafana.

**Safety boundary:** No browser/Pi/Grafana endpoint can directly authorize, modify or release physical PWM. A locally approved fan profile is the only persistent control path. The Laing DDC pump remains tachometer-only; a fan pump mapping forbids manual PWM commissioning. Firmware restoration is best-effort on clean shutdown and interrupted manual tests; no software can guarantee recovery from sudden power loss.

## Getting started

Build from the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet restore src/SensorDash/SensorDash.csproj -r win-x64
dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
```

The GitHub Actions Windows workflow runs JavaScript tests, C# self-tests, MSI packaging, HTTP/IPC smoke tests, and a real MSI upgrade/rollback check against a successful Production artifact. Use the feature-branch MSI only after CI has passed and a supervised hardware-control validation window is available. Keep your existing approved curves, service data and last working installer until radiator/NVMe/pump parity has been proven.

The loopback UI is `http://127.0.0.1:8765`. Physical output requests remain behind the administrator-authorized local fan-control boundary. The elevated service should keep running if the tray exits or the Pi disappears.

## Cooling control and sleep/resume

The fan controller uses cached configuration snapshots that require explicit approval before becoming active. Routine changes to aliases, cooling assignments or draft curves never silently change an approved PWM target. The collector samples physical sensors and controls on its own hardware thread; the Pi is not involved.

After a polling gap consistent with sleep/resume, persistent already-approved profiles force a fresh LHM `SetDefault` → `SetSoftware` transition (because a cached software mode may lie after wake). A short **manual commissioning session** is instead terminated on a large collection gap and attempts to restore firmware control. Local bounded diagnostics capture requested PWM, last command, reported LHM mode and measured tachometer where assigned. This is not a guarantee of actual electrical PWM; verify on physical hardware.

## Monitoring, storage and events

The collector still writes a **bounded local SQLite recording spool** under `%ProgramData%\SensorDash`; it is necessary for durable export cursor/retry while the Pi is offline. Settings retain recording mode, sample interval, retention and archive exclusions. Storage/NVMe polling is separately configurable. The long-term history viewer and storage diagnostics live in Grafana.

The outbound telemetry worker does **not** poll hardware a second time. It asynchronously sends recorded numeric samples, cooling bindings, stable-ID metadata (including physical device type), estimates tagged by calibration revision, event warning/recovery impulses and local spool-file-size gauges. It uses HTTPS with a Pi-issued private-CA certificate and bearer token. Credentials are never sent in GET responses or placed in Grafana series labels.

Go to **Settings → Pi telemetry export** to configure the receiver, computer label, optional token and CA. The sender detects changes to the configuration/credential file contents and reopens **only its HTTPS session**; the privileged hardware collector keeps running without restart. The UI reports last delivery, current exporter state, retries and configuration reloads. It never restarts the fan controller.

See [telemetry setup](docs/telemetry-export.md) and [control/observability ownership ADR](docs/adr/0008-local-control-observability.md).

## Development and rollout

- [Project architecture](docs/project.md), [development/CI](docs/development.md) and [hardware drivers](docs/hardware-drivers.md).
- [Cooling hardware presets](presets/README.md), [power curves](docs/power-curves.md) and [release identity](docs/adr/0002-msi-release-identity.md).
- Deploy Pi intake/schema changes **before** the updated Windows exporter so older and new metrics are accepted throughout the rollout.
- This is a **draft branch**, not an installed/validated Windows fan-control replacement. Ensure stable radiator RPM/duty, pump tach-only, NVMe fan, tray icons, and sleep/resume recovery on the actual machine before merging Production.

