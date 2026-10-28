# SensorDash 0.6.1 — Persistent multi-fan control and responsive charts

The old **Curve Sandbox** screen is now **Fan Control**, with continuous service-owned, administrator-approved fan profiles. It no longer limits actual profile operation to 120 seconds or 70–100% duty; those restrictions remain only for optional manual hardware commissioning tests.

## Continuous fan profiles

- Create as many independent profiles as needed (up to 20), with up to 12 physical PWM outputs and 8 temperature curves per profile.
- Each curve references a discovered physical or virtual temperature source. The collector evaluates all its curves and takes the **maximum requested PWM percentage**, so CPU and GPU can jointly control radiator fans.
- Supports a configurable minimum fan duty, temperature hysteresis (°C), immediate increases, and smoothed decreases. No fan-stop/zero RPM automation is enabled by default.
- Unavailable or stale temperature source: request **100% duty (or the maximum supported by that controller)** until the source recovers. Outputs that disappear or cannot be written are reported as degraded rather than silently ignored.
- A profile is a normal editable draft until clicked **Enable profile**, which requests Windows UAC. The elevated collector snapshots the selected curves and physical outputs and persists the approval separately in `%ProgramData%\SensorDash\approved-fan-profiles.json`.
- Approved profiles **resume after service restart**. Merely editing or deleting a draft does not change approved hardware settings; select **Approve updated profile** to apply changes, or **Stop** to restore firmware/default control.
- Stopping an output/profile and clean service shutdown call LibreHardwareMonitor's `SetDefault()`. Firmware-level recovery after a hard crash/power loss remains dependent on the actual motherboard; BIOS fan safety remains important.
- A writable PWM output can be assigned to at most one approved profile. Optional manual tests cannot commandeer outputs already owned by approved profiles. SensorDash does not guess physical fan headers.
- The DDC pump can remain monitored as a tachometer without an associated writable pump output.

## Included optional curve starters (exact earlier values)

**Shared radiator fans:** Create a CPU curve (40°C/20%, 65/20, 70/25, 75/35, 80/50, 85/75, 90/100) and GPU-hotspot curve (40°C/10%, 55/10, 60/20, 65/25, 70/35, 75/50, 85/75, 95/100). Combine them in one profile via MAX. Temperature hysteresis is 3°C, falling-speed response 10 seconds. The source sensor IDs are suggested from detected hardware; physical output assignments remain blank until verified.

**NVMe cooler:** Create separate curve (35°C/30%, 40/30, 45/40, 50/55, 55/75, 60/100) with its own output assignment. Storage-sensor age and staleness are enforced, and the output goes to full available speed if the temperature stops updating.

Starter profiles are **opt-in** and hardware-neutral. Installations do not automatically create or activate these profiles.

## Graphs, performance and release labels

- Hover tooltips display sensor names beside their color swatches and current values, rather than colors and numbers alone.
- History queries cache sensor/range pairs for 60 seconds, survive page navigation and reuse in-flight requests. Missing IDs are batched 12 at a time. The cache is bounded to 120 entries; a manual Refresh discards it.
- Sensor frames and chart painting remain independent. While dragging or hovering a graph, periodic frame updates do not reset the canvas.
- Sidebar and footer now say **Fan Control**, show the installed version from the running executable, and indicate whether persistent profiles or temporary manual control are active.
- No physical fan commands are issued merely by installing or opening the UI. The Windows build tests remain manually triggered or on merge/push to Production only.
- Version 0.6.1 spreads chart rendering across animation frames and adds on-demand desktop/collector memory plus browser chart/cache diagnostics in Settings. It does not change the sensor collection rate or fan-control logic.

**Upgrade:** Close the tray application; install the v0.6.1 MSI. Existing SQLite history, aliases, workspaces, tray readouts, and saved temperature curve drafts remain in place. Quit the separate Fan Control application before enabling the corresponding SensorDash output(s).

---

# SensorDash 0.5.1 — More accurate fan takeover and simpler safety UI

LibreHardwareMonitor IControl reports Undefined for newly initialized controls unless its own settings say otherwise. This is an internal library state, **not a measurement of whether firmware is active or a competing fan controller exists**. SensorDash v0.5.0 mistakenly required Default and conflated mode with supported PWM range; v0.5.1 accepts Undefined, Default and Software modes after explicit user confirmation that other fan-control software is closed.

Output eligibility now uses the channel's declared minimum/maximum PWM range; manual test duty must be inside the channel's supported range and the 70–100% commissioning band. A first takeover from LHM Software mode explicitly transitions through Default so a cached identical SoftwareValue cannot suppress the actual register write. Restore operations remain best-effort; an abrupt process crash cannot run cleanup.

UI wording is intentionally calmer: acknowledge only that other fan-control software is closed and you wish SensorDash to control the output. The conservative 70% test floor and 120-second session limit remain until physical PWM output behavior is validated.

---

# SensorDash 0.5.0 — Experimental Live Fan Control (prior release notes)

Fan Control (the separate app) can simply be closed. Quit its tray/background process and disable its automatic startup before commanding a channel with SensorDash. No migration integration is required. Confirm the fan remains operational under BIOS/default control.

This release introduces the first writable-channel implementation, not a complete unattended replacement for Fan Control.

### Supported experimental workflow

1. Keep SensorDash Collector installed and running. Open http://127.0.0.1:8765/#curves.
2. Discover actual IControl-backed hardware channels. An RPM-only Fan reading is not a writable output.
3. Exit any competing fan-controller application. The LHM control object's mode may report Undefined, Default or Software; this is not a reliable cross-process ownership indicator. Verify actual fan behavior.
4. Select one output. Choose Manual PWM or a saved temperature curve, then a 30/60/120-second test.
5. Acknowledge exclusive ownership and click Request elevated takeover. Windows presents UAC for a one-shot administrator helper.
6. Observe status and fan response. Release the channel to firmware control or allow the lease to expire.

### Security and limitations

- No persisted fan takeovers. Every live session requires an admin-approved UAC prompt, lasts at most two minutes and commands only one channel.
- Initial duty floor 70–100%, even when a saved curve calculates a lower duty. This is a test safety restriction, not a quiet long-term cooling profile.
- A separate named pipe (SensorDash.Fan.Admin.v1) permits only local Windows administrators and SYSTEM to request writes. The ordinary telemetry IPC exposes read-only output inventory/status, not write commands.
- The loopback browser command requires same-origin JSON with a custom header, and only launches the UAC helper; it cannot itself write to hardware.
- All LibreHardwareMonitor SetSoftware/SetDefault operations run on the collector's hardware thread.
- Authorized curves are snapshotted upon elevated acceptance, preventing ordinary settings writes from changing a privileged live lease.
- On lease expiry, explicit release, service shutdown, sensor failure or hardware reinitialization the collector attempts firmware/default restoration. This is BEST EFFORT, not a hardware watchdog: power loss, process crash or a stalled driver can prevent it.
- Fan RPM sensors and PWM control channels are not automatically paired. Confirm physical wiring and fan behavior before using a channel, and do not take over essential pumps until fallback behavior is verified.
- Not yet implemented: silent startup control, unlimited continuous operation, multi-channel control, low-duty/zero-RPM stop modes, independent external watchdog, calibration and automatic RPM matching.

### Validation

The hardware-free self-test checks that fan controls stay disabled by default and rejects unauthorized or unknown channels. Windows MSI workflow verifies admin-only IPC, explicit UAC action, read-only inventory and service installation, without writing any fan hardware on CI.

Upgrade from v0.4.0 by exiting the tray first and running the v0.5.0 MSI. Historical data and saved configurations remain in ProgramData.

---

# SensorDash — General capabilities (0.6.x)

A local-first Windows hardware telemetry dashboard with configurable cooling systems, dashboards, sensor discovery, tray readouts and **previewable, administrator-approved fan profiles**. The collector is an elevated Windows Service; the browser dashboard and notification-area application run as a standard user. Built with .NET 10, LibreHardwareMonitorLib, SQLite and a lightweight embedded web UI.

**Development version: 0.6.1.** The feature branch may be ahead of the most recently validated installer. The standard MSI pipeline runs on `Production` pushes or manual dispatch. Temporary validation branches may deliberately enable a one-off build; routine feature commits do not trigger it.

## What Cooling Systems are for

A **Cooling System** is an analytical view of related hardware measurements, not merely a named folder of sensors or a literal inventory of every mechanical part. Create as many liquid, air, or custom cooling systems as needed. Each system consists of a user-defined set of components, with a small number of meaningful roles:

| Component role | Appropriate sensor assignments | What it contributes |
| --- | --- | --- |
| Heat source | Temperature, power, load, clock | Hardware heat source readings and historical graphs |
| Fan group | Fan RPM, available output duty, optional temperature | Fan performance and response over time |
| Pump | Pump RPM, flow rate, reported duty, optional temperature | Circulation and pump monitoring |
| Temperature probe | Temperatures (coolant, ambient, inlet, outlet, etc.) | Water/air temperature history |
| Flow meter | Flow readings | Flow trend |
| Other hardware | Any compatible sensor | Specialized hardware without a preset role |

Passive radiators are **not** a required component type: a radiator is only relevant through measurements provided by its fans, probes and associated system. The role labels guide assignment and visualization; no assumptions about a particular CPU, GPU, coolant loop, number of radiators or fan controller are embedded.

Each cooling system automatically displays a telemetry summary and independent temperature, power, RPM, duty and flow charts **where those sensor types are assigned**. It can also **Create dashboard**, which builds ordinary editable dashboard widgets from the current associations. That created dashboard is a snapshot of the assignments; the Cooling System itself always reflects its current components. Power measurements are shown individually and not blindly summed because sensor readings may overlap (e.g., package and core power). Temperature and flow charts never imply physically measured cooling capacity.

## Sensors, usage and visibility

**Sensor Explorer** discovers every reading the collector reports. It can group by hardware or sensor type, filter to a selected device, search by hardware/name/ID/usage, and quickly show **In use** or **Hidden** readings. Generic hardware names such as "Temperature #1" come from the hardware library; default Overview cards prioritize descriptive names, while Explorer lets you add aliases.

The **Used in** section of a sensor lists every assignment to named dashboards and widget titles, cooling systems and component names, tray readouts, virtual sensors, simulated curves, and monitoring thresholds. Clicking a cooling-system reference navigates directly to the **specific component** and highlights it. One physical sensor may be referenced in multiple places without duplication.

Visibility, collection and history are deliberately **different settings**:

1. **Hide sensor, device or category** — presentation-only. Configured dashboards and curves still receive data.
2. **Disable collection for a hardware family** — turns off that LibreHardwareMonitor `Computer` category and reopens discovery; all readings for that family disappear until reenabled. All eight current families use the same generic UI: CPU, GPU, Motherboard/Super I/O, Memory, Storage, Network, Controllers and Power Monitors.
3. **Pause an individual discovered hardware device** — skip that device's periodic `Update()` and expose its sensor names with unavailable values. Underlying driver and parent-device behavior can still require shared work.
4. **Choose SQLite history policy** — Balanced (default), All, or In-use-only, with independent hardware-type exclusions. This reduces future database writes without necessarily changing hardware polling. Retained old samples expire on normal cleanup.

There is no misleading single-sensor "hardware polling" toggle: the hardware library typically performs updates at device granularity.

## Dashboards and tray

Dashboards contain configurable metric, chart and live-reading widgets; create a blank dashboard or use thermal, cooling or storage templates. **Lock layout** hides widget edit/add/remove controls while preserving visualization. It is a UI convenience rather than an authorization boundary.

Pin up to six independently configured Windows notification-area **numeric readouts**. Select the physical or virtual sensor, label, accent color and precision. Short, high-contrast digits optimize readability at native 16–32 px tray sizes. Hovering shows the complete value and name. The main tray menu opens the dashboard, reports collector status and closes the desktop UI without stopping logging.

Fan curves use an editable **Temperature (°C) / Fan speed (%) numeric table** with add/remove rows, range checks, live preview and 1 Hz simulation. Simulations never write to outputs. Saved curves can be added to persistent fan profiles, which require administrator approval and exclusive ownership of verified physical PWM channels. Do not run competing fan controllers on the same outputs.

## Installation and data

A successful [Windows build](https://github.com/CodyMathis123/SensorDash/actions/workflows/windows-build.yml) produces the MSI. To install or upgrade, exit the SensorDash tray, run the MSI and approve administrator access. It installs an automatic `SensorDashCollector` Windows Service, the branded `SensorDash` all-users Start Menu shortcut, and a tray autostart entry. Uninstall removes those managed components.

The service's SQLite history and settings live under `%ProgramData%\SensorDash`. They are retained across MSI updates. Prior prototype-specific preferences are not maintained with compatibility shims; adjust any old custom configurations using the new editors if needed.

The collector and desktop communicate over an ACL-restricted local named pipe. The elevated collector has no HTTP listener. The standard-user dashboard listens only on `http://127.0.0.1:8765/`. Hardware monitoring, fan-curve simulation, and locally approved fan profiles work without cloud access. The MSI does not install, replace or configure Fan Control or PawnIO.

## Build and test

On Windows with the .NET 10 SDK:

```powershell
node --check src/SensorDash/wwwroot/app.js
node --check src/SensorDash/wwwroot/workspace.js
node scripts/test-workspace.cjs
dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
.\dist\SensorDash.exe --self-test
dotnet build installer/SensorDash.wixproj -c Release -p:Platform=x64
```

The JavaScript regression test covers auto-generated cooling analytics, meaningful component types, in-use sensor references, exact Explorer→Cooling navigation, history query batching, hidden sensors, layout locks and generated dashboard widgets. The executable self-test covers virtual sensor arithmetic, simulated curves, missing/stale readings, tray formatting, collection groups, history filtering and cooling component normalization.

The full Windows workflow also smoke-tests the service-to-dashboard named pipe and verifies MSI install, service startup, Start Menu shortcut creation, tray registration and uninstall. It runs on **`Production` pushes or manual dispatch** once the workflow exists on the default branch. A validation branch can temporarily enable its own build trigger. Ordinary feature commits do not trigger it.

This application is not safety-rated fan-control software. Live fan profiles are implemented, but physical PWM channel mapping, fan startup at low duty, ownership, firmware-level recovery and fail-safe response must be verified on each target machine before relying on unattended control.
