# SensorDash — Windows hardware control companion

SensorDash is a Windows hardware-monitoring and control utility for [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor). Its elevated collector is the **single authority** for sensor polling, local cooling topology, approved fan curves and physical PWM outputs. The collector service also hosts the loopback dashboard. A native Win32 tray executable displays sensor readouts in each logged-in user's session. The Raspberry Pi hosts [HomeAwayFromHome](https://github.com/CodyMathis123/HomeAwayFromHome), Grafana and VictoriaMetrics for read-only history, infrastructure health and alert analysis.

## Ownership boundary

| SensorDash on Windows | HomeAway / Grafana on Pi |
|---|---|
| Fan curves, profile approval and firmware fallback | Historical sensor measurements and recorded maxima |
| Cooling-system names, roles, associations and power-model calibration | Cooling trends and estimated fan/pump electricity |
| Sensor discovery, aliases, thresholds and recording policies | Sensor metadata, historical event impulses and archive statistics |
| Tray readings, live cooling status and local alert messages | Storage-device temperature/activity trends and monitoring |
| Outbound TLS exporter URL/host/credentials | Pi storage usage, scrape status, data retention and dashboards |

The Windows interface has **Cooling Systems, Fan Control, Tray Readouts, Sensor Explorer, Events and Settings**. The old Windows Overview, custom Dashboards, Virtual Sensors and Storage/history chart pages were deliberately removed. We no longer ship the client history-chart engine or its associated static web assets. Long-term visualization is owned by Grafana.

**Safety boundary:** Only Windows-authenticated, explicitly confirmed local browser requests may ask the service to modify physical PWM. Remote Pi/Grafana endpoints remain read-only. A locally approved fan profile is the only persistent control path. The Laing DDC pump remains tachometer-only; a fan pump mapping forbids manual PWM commissioning. Firmware restoration is best-effort on clean shutdown and interrupted manual tests; no software can guarantee recovery from sudden power loss.

## Paired HomeAway end-to-end test

The **complete feature checkout for this repository** is `chore/sensordash-telemetry-docs`. It includes the controls-only + HTTPS-export PR #13 and its documentation cleanup PR #14; use it with HomeAway's `chore/docs-and-legacy-cleanup` branch for the complete Pi/Grafana/Authelia/PC-control stack. Neither `Production` branch yet contains the whole migration.

For a fresh Windows checkout:

```powershell
git clone -b chore/sensordash-telemetry-docs https://github.com/CodyMathis123/SensorDash.git
```

Before installing on the actual fan-controlling PC, review the [single two-repo integration and rollback guide](https://github.com/CodyMathis123/HomeAwayFromHome/blob/chore/docs-and-legacy-cleanup/docs/INTEGRATION-TEST.md), deploy the matching Pi receiver schema first, and use the validated CI-produced MSI from [PR #14](https://github.com/CodyMathis123/SensorDash/pull/14) rather than an old Production installer. SensorDash PR #12 (LAN/mobile Windows dashboard) is **not part of this pairing**: it targets the older Windows history UI and must be reviewed separately, not merged blindly.

## Getting started

Build from the repository root on Windows with the .NET 10 SDK:

```powershell
dotnet restore src/SensorDash/SensorDash.csproj -r win-x64
dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
```

The manually requested GitHub Actions Windows workflow runs JavaScript tests, C# self-tests, MSVC native tray compilation, service-hosted HTTP/Windows-auth smoke tests, MSI packaging, and installer upgrade/repair/downgrade-rejection tests against a previous Production artifact. Use the feature-branch MSI only after CI has passed and a supervised hardware-control validation window is available. Keep your existing approved curves, service data and last working installer until radiator/NVMe/pump parity has been proven.

The loopback UI is `http://127.0.0.1:8765`, hosted by the SYSTEM collector service. Browser mutations use Windows Negotiate authentication and action-specific, same-origin JSON checks. Fan-control changes also require unmistakable in-browser confirmation and are checked again by the collector. No UAC helper is launched for each change. The collector keeps working when the tray exits, the user signs out, or the Pi disappears.

## Experimental native-tray/service layout

The collector still installs as `SensorDash.exe --collector`, preserving the
existing Windows Service registration and ProgramData settings/telemetry buffer.
It now also serves the loopback web dashboard. The old WinForms desktop host
and general-user JSON IPC bridge are not compiled into the service.

`SensorDash.Tray.exe` is a small native C++ Win32 process with no CLR,
WinForms, ASP.NET Core, or hardware-driver dependency. Windows launches it
at each interactive sign-in through the MSI's Run registry registration.
It has a separate Start Menu shortcut, a per-session single-instance mutex,
configurable sensor icons, and a simple read-only HTTP status feed.
Exiting the tray never stops cooling, telemetry or the web dashboard.

The new architecture is being validated on
`feature/service-hosted-web-native-tray`. The previous validated MSI is
a rollback source; newer MSI product versions reject direct downgrades, so
returning to an older MSI requires a planned uninstall/reinstall (with settings
preservation confirmed separately). Do not replace the working cooling service
with an unvalidated feature-branch MSI.

## Cooling control and sleep/resume

The fan controller uses cached configuration snapshots that require explicit approval before becoming active. Routine changes to aliases, cooling assignments or draft curves never silently change an approved PWM target. The collector samples physical sensors and controls on its own hardware thread; the Pi is not involved.

After a polling gap consistent with sleep/resume, persistent already-approved profiles force a fresh LHM `SetDefault` → `SetSoftware` transition (because a cached software mode may lie after wake). A short **manual commissioning session** is instead terminated on a large collection gap and attempts to restore firmware control. Local bounded diagnostics capture requested PWM, last command, reported LHM mode and measured tachometer where assigned. This is not a guarantee of actual electrical PWM; verify on physical hardware.

## Monitoring, storage and events

The collector still writes a **bounded local SQLite recording spool** under `%ProgramData%\SensorDash`; it is necessary for durable export cursor/retry while the Pi is offline. Settings retain recording mode, sample interval, retention and archive exclusions. Storage/NVMe polling is separately configurable. The long-term history viewer and storage diagnostics live in Grafana.

The outbound telemetry worker does **not** poll hardware a second time. It asynchronously sends recorded numeric samples, cooling bindings, stable-ID metadata (including physical device type), estimates tagged by calibration revision, event warning/recovery impulses and local spool-file-size gauges. It uses HTTPS with a Pi-issued private-CA certificate and bearer token. Credentials are never sent in GET responses or placed in Grafana series labels.

Go to **Settings → Pi telemetry export** to configure the receiver, computer label, optional token and CA. The sender detects changes to the configuration/credential file contents and reopens **only its HTTPS session**; the privileged hardware collector keeps running without restart. The UI reports last delivery, current exporter state, retries and configuration reloads. It never restarts the fan controller.

See [telemetry setup](docs/telemetry-export.md) and [control/observability ownership ADR](docs/adr/0008-local-control-observability.md).

## Feature delivery status

This controls-only + outbound telemetry design is implemented in [SensorDash PR #13](https://github.com/CodyMathis123/SensorDash/pull/13); it is **not yet the released `Production` MSI**. The separate [LAN/mobile dashboard PR #12](https://github.com/CodyMathis123/SensorDash/pull/12) targets the previous browser dashboard and requires a deliberate compatibility review with the controls-only UX before any merge. Do not treat the two as independent toggles or install both builds interchangeably. On Windows, use the CI-produced MSI only after its current revision passes validation; live fan/wake safety checks still belong to the operator.

## Development and rollout

- [Project architecture](docs/project.md), [development/CI](docs/development.md) and [hardware drivers](docs/hardware-drivers.md).
- [Cooling hardware presets](presets/README.md), [power curves](docs/power-curves.md) and [release identity](docs/adr/0002-msi-release-identity.md).
- Deploy Pi intake/schema changes **before** the updated Windows exporter so older and new metrics are accepted throughout the rollout.
- This is a **draft branch**, not an installed/validated Windows fan-control replacement. Ensure stable radiator RPM/duty, pump tach-only, NVMe fan, tray icons, and sleep/resume recovery on the actual machine before merging Production.


## Reporting contract (HomeAway / Grafana)

The HomeAway [SensorDash reporting summary](https://github.com/CodyMathis123/HomeAwayFromHome/blob/feature/sensordash-reporting-summary-20261101/grafana/dashboards/reporting-summary.json) is a read-only consumer of the existing exporter. Use Grafana's 24-hour, 7-day or 30-day time range to compare observations. It relies on immutable sensor IDs for time-series joins and on the published `device_type=Cpu` / `device_type=Gpu` metadata for hardware family identification; **do not parse vendor-specific sensor IDs or assume that a single "CPU temperature" probe exists**. Aliases remain defined in SensorDash and are display-only.

Recorded minimum, average and maximum are statistics **of available samples**, not proof of continuous use. The reported count and recording-density graph can expose gaps, but sample counts alone cannot measure uptime or coverage because different sensors have different collection cadences. Empty or offline periods must be shown as missing, never silently converted to zero. CPU/GPU watts originate from hardware sensors; fan and pump watts may be model estimates with separate `source` and calibration `revision` labels.

A wall-clock energy total must be derived only from actual timestamp pairs with a bounded interval and clearly stated coverage, like the existing `HistoryStatsAccumulator` behavior. Do not compute kWh by multiplying arbitrary snapshots by a dashboard duration or fill sleep gaps. `sensordash_spool_value` covers the Windows SQLite database/WAL/SHM footprint; it does not imply VictoriaMetrics can accurately attribute compressed disk bytes to individual sensors. SensorDash retains the exporter retry spool and all cooling/PWM control authority; dashboards cannot edit profiles.
