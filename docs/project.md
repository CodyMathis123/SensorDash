# Project and source map

SensorDash is the local Windows hardware-control and telemetry-export companion to Grafana on the Pi. It retains the elevated collector, tray, local configuration UI and SQLite retry buffer, not duplicate general monitoring dashboards. See [ADR 0008](adr/0008-local-control-observability.md). It uses .NET 10 Windows/Windows Forms, LibreHardwareMonitor, Microsoft.Data.Sqlite and plain JavaScript/CSS. The web assets are embedded by `SensorDash.csproj`; new web modules must also be registered there.

## Source tree

| Location | Responsibility |
| --- | --- |
| `src/SensorDash/Program.cs` | Windows service/dashboard startup, local APIs, settings, history-spool and export integration |
| `src/SensorDash/HardwareCollection.cs` | Hardware collection selection |
| `src/SensorDash/ServiceBridge.cs`, `AdminFanControl.cs` | Collector communication and administrator control boundary |
| `src/SensorDash/FanControlRuntime.cs`, `FanPwmChannels.cs` | Hardware control execution and channel adapters |
| `src/SensorDash/FanProfilePlanner.cs`, `FanCurveMath.cs`, `PersistentFanProfiles.cs` | Planning, interpolation and durable approved profiles |
| `src/SensorDash/WorkspaceModels.cs` | Workspace/cooling configuration models |
| `src/SensorDash/HistoryStatistics.cs`, `CoolingEventRecorder.cs` | History aggregation and cooling events |
| `src/SensorDash/*SelfTest.cs` | Hardware-free backend regression checks |
| `src/SensorDash/wwwroot/app.js` | Compact local control UI, navigation, sensor status and telemetry/export settings |
| `src/SensorDash/wwwroot/workspace.js` | Workspace editors and serialized settings persistence |
| `src/SensorDash/wwwroot/cooling-electrical.js`, `cooling-insights.js` | Local cooling/electrical configuration and live feedback; no history charts |
| `src/SensorDash/wwwroot/fan-*.js` | Commissioning, feedback, approval UX and diagnostics |
| `src/SensorDash/TelemetryConfiguration.cs`, `TelemetryExport.cs`, `TelemetryMetadata.cs` | Outbound read-only measurements, config reload, catalog metadata, bounded retries |
| `scripts/` | Node regressions and build/packaging helpers |
| `tools/SensorDashIconCarrier/` | Windows icon resource helper |
| `.github/workflows/windows-build.yml` | Windows build, self-tests, packaging and installer validation |
| `docs/adr/` | Architectural decisions |

## Important boundaries

The browser edits configuration drafts. Approved fan profiles are separate collector-owned snapshots, persisted under `%ProgramData%\SensorDash`; changing a draft does not authorize a live output. Layout locks are editing conveniences, not security controls. Preserve saved user settings and history across upgrades.

Read the relevant source and tests for exact current behavior. This map describes responsibilities, not an exhaustive generated file inventory. Operator setup is in the root README and [telemetry export guide](telemetry-export.md); release identity and architecture rationale live in [ADRs](adr/README.md).

`AccessoryPowerHistory.cs` computes gap-aware fan/pump energy from recorded measurements and configuration. [UI guidance](ui-guidelines.md) defines concise copy and accessible controls.
`CoolingElectricalConfig.PowerCurve` stores per-unit speed/power points; see [power curves](power-curves.md) for source data and interpolation rules.

`presets/cooling/*.json` contains bundled fan/pump specifications. `CoolingPresetCatalog.cs` serves embedded snapshots; [contribution guide](../presets/README.md), schema and `scripts/test-cooling-presets.cjs` define the data contract.

`PawnIoStatus.cs` probes optional shared driver availability in the collector. `driver-status.js` presents actionable status without rebuilding active forms; see [hardware driver setup](hardware-drivers.md).

New remote configuration and redacted status live in TelemetryConfiguration.cs and same-origin /api/telemetry-config; time-stamped event export and local spool-file sizes live in TelemetryExport.cs. Current sensor metadata, including device_type=Storage, lives in TelemetryMetadata.cs. Long historical analysis is owned by Grafana.

**Controls-only cleanup:** redundant `/api/history*` and history IPC routes and the browser chart files (`chart-gaps.js`, `chart-time-axis.js`, `cooling-timeline.js`, `history-insights.js`) have been removed. The Windows process still archives records for the separate telemetry exporter. Pi exporter settings are dynamically observed; a change reopens only the outbound HTTPS client without a service restart. Timed manual commissioning is released on sleep/collection gaps; approved profiles retain their existing rearm policy.
