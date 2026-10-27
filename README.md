# SensorDash

A local-first Windows hardware telemetry dashboard with configurable cooling systems, dashboards, sensor discovery, tray readouts and **simulation-only fan curves**. The collector is an elevated Windows Service; the browser dashboard and notification-area application run as a standard user. Built with .NET 10, LibreHardwareMonitorLib, SQLite and a lightweight embedded web UI.

**Development version: 0.4.0.** The feature branch may be ahead of the most recently validated installer. The full MSI pipeline runs manually or on a push to `Production`, not on every feature commit.

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

Fan curves use an editable **Temperature (°C) / Fan speed (%) numeric table** with add/remove rows, range checks, live preview and 1 Hz simulation. The simulator handles missing/stale inputs and doesn't write to fan or pump control outputs. **Continue using Fan Control or your other existing controller for real fan control.**

## Installation and data

A successful [Windows build](https://github.com/CodyMathis123/SensorDash/actions/workflows/windows-build.yml) produces the MSI. To install or upgrade, exit the SensorDash tray, run the MSI and approve administrator access. It installs an automatic `SensorDashCollector` Windows Service, the branded `SensorDash` all-users Start Menu shortcut, and a tray autostart entry. Uninstall removes those managed components.

The service's SQLite history and settings live under `%ProgramData%\SensorDash`. They are retained across MSI updates. Prior prototype-specific preferences are not maintained with compatibility shims; adjust any old custom configurations using the new editors if needed.

The collector and desktop communicate over an ACL-restricted local named pipe. The elevated collector has no HTTP listener. The standard-user dashboard listens only on `http://127.0.0.1:8765/`. Hardware monitoring and fan-curve **simulation** work without cloud access. The MSI does not install, replace or configure Fan Control or PawnIO.

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

The full Windows workflow also smoke-tests the service-to-dashboard named pipe and verifies MSI install, service startup, Start Menu shortcut creation, tray registration and uninstall. It runs **only on `Production` pushes or manual dispatch**, to avoid expensive builds on every development commit.

This application is not safety-rated fan-control software. Hardware writes, output ownership, calibration, watchdog recovery and tighter control authorization must be implemented and separately tested before any future live fan-control release.
