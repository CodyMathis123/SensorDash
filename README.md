# SensorDash v0.2.2 — Branded application and Start Menu shortcut

Version 0.2.2 embeds the same cyan-on-navy telemetry icon used by the tray directly into **SensorDash.exe**. The per-machine MSI explicitly registers an **all-users Start Menu shortcut** called **SensorDash** in `%ProgramData%\Microsoft\Windows\Start Menu\Programs\SensorDash.lnk` and assigns the branded icon using the MSI Icon table.

The MSI install/uninstall test checks that the shortcut is actually created, then removed on uninstall. It also verifies the published multi-resolution `SensorDash.ico` and extracts the executable's embedded icon to check the application resource.

The icon is generated reproducibly during the Windows build by `scripts/Generate-SensorDashIcon.ps1`; no external downloadable icon asset is required. Upgrading from 0.2.1 through the new MSI retains the collector data under `%ProgramData%\SensorDash`. **Exit the tray application before upgrading** so Windows Installer can replace its executable.

---

# SensorDash v0.2.1 — Tray usability update

The tray menu now contains **Open SensorDash**, a **disabled collector status indicator** (Online with the discovered sensor count, Discovering, Sensor error, or Offline), and **Exit tray and dashboard**. The previous "Collector service: SensorDashCollector" entry had incorrectly duplicated Open Dashboard.

SensorDash now renders a bespoke turquoise-on-navy pulse/monitoring icon for the Windows notification area. No external icon file is needed—the icon is generated in the client process with GDI+, and its native handle is cleaned up correctly. The tray polls only the collector's lightweight IPC status every 10 seconds, independently of full sensor sampling, and reports connection problems without modifying fan control.

Install **SensorDash-Setup-0.2.1-x64.msi** over 0.2.0 to upgrade; MSI major-upgrade registration handles the existing installation. The collector history under %ProgramData%\\SensorDash is preserved. To see the new tray UI immediately, exit any existing SensorDash tray process and launch it from the Start Menu after installing. Rebooting or signing out and in also starts the updated version.

---


# SensorDash 0.2.0 — MSI / Windows service / tray dashboard

SensorDash is a read-only Windows hardware telemetry application. Fan Control continues to own your fan curves.

## Install

1. Download SensorDash-Setup-0.2.0-x64.msi from the successful Windows Actions run.
2. Double-click the MSI and approve administrator access once.
3. The MSI installs SensorDash.exe into Program Files, registers and starts the SensorDashCollector Windows service under LocalSystem, adds a Start Menu shortcut and configures tray startup at Windows sign-in.
4. Open SensorDash from Start Menu to launch the tray and the browser dashboard at http://127.0.0.1:8765.
5. Double-click the tray icon to reopen the browser. Right-click it to exit the tray without stopping telemetry collection.

No separate .NET runtime or Python is required.

## Upgrades, uninstall, versioning

- WiX MSI major upgrades replace installed binaries and service registration, and disallow downgrades.
- Version 0.2.0 follows semantic versioning. Each GitHub Actions build publishes a Windows MSI and portable developer artifact.
- Uninstall through Windows Settings / Apps / Installed apps. MSI removes the Windows Service, the autorun entry, app binary and Start Menu shortcut.
- New history and settings live in %ProgramData%\SensorDash. These are deliberately preserved during uninstall/upgrade.
- The MSI does not uninstall or modify PawnIO or Fan Control.
- The service runs even before login. The user-facing dashboard runs without elevation.

## Transition from 0.1.x portable ZIP

Before installing, exit old SensorDash processes, uninstall old startup shortcut and any prior optional "SensorDash (Elevated)" scheduled task. The old files in %LOCALAPPDATA%\SensorDash remain untouched. Version 0.2.0 starts a new machine-wide SQLite history in %ProgramData%\SensorDash; automatic migration of old history and aliases is not implemented yet. Back up legacy data if you wish to retain it.

## Architecture and security

- SensorDash.exe --collector: LocalSystem Windows Service; hardware polling, SQLite, events, validated settings.
- SensorDash.exe --desktop: regular Windows account; ASP.NET Core localhost dashboard and tray icon.
- IPC: Windows named pipe SensorDash.Telemetry.v1, explicit access for local authenticated users (read telemetry and set validated monitoring options only). No arbitrary filesystem, process execution, or fan control commands.
- The elevated collector has no HTTP listener. Only the regular desktop process listens on 127.0.0.1:8765.
- Local authenticated users can modify monitoring settings through the pipe. It is designed for a trusted, single-user workstation.
- The collector retains its logs when the dashboard closes or you log off.

## Build from source

Windows with .NET 10 SDK:
- dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
- dotnet build installer/SensorDash.wixproj -c Release -p:Platform=x64
- GitHub Actions builds both automatically and smoke-tests collector + dashboard named-pipe communication.

Licenses: LibreHardwareMonitorLib MPL 2.0; WiX 5.0.2 (the last generation before newer WiX maintenance-fee licensing). A newer WiX toolchain would require independent license review.

---

## Portable v0.1.x historical notes

# SensorDash

A locally hosted Windows hardware-monitoring dashboard powered by [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor). Runs under your Windows user account, with **no Libre Hardware Monitor GUI, Python, cloud, or fan-control writes required**.

## Download and run
1. Open **Actions → Windows portable build → most recent successful run → SensorDash-win-x64** and download the artifact ZIP.
2. Extract **the whole folder** anywhere (e.g. \`C:\Tools\SensorDash\`).
3. Double-click **SensorDash.exe** (or **Start-SensorDash.cmd**). It hosts the local dashboard and opens **http://127.0.0.1:8765**.
4. Optional: Double-click **Install-Startup.cmd** to auto-start at Windows sign-in (without opening a browser). Remove via **Remove-Startup.cmd**.
5. To exit, use Task Manager → SensorDash or stop the process from PowerShell (\`Stop-Process -Name SensorDash\`). You can relaunch it by double-clicking.

No administrator rights are required to *start* SensorDash, but certain low-level sensors/drivers might be unavailable without elevation. The dashboard shows errors rather than inventing readings. If it fails to start, look at the log in \`%LOCALAPPDATA%\SensorDash\SensorDash.log\`.

## What it does
- Polls LibreHardwareMonitorLib sensors directly; fast sensors typically every 2 seconds, storage devices every 10 seconds.
- Shows live temperatures, load, CPU/GPU package power, fan and pump tachometers, and NVMe readings.
- Persists selected numeric readings into SQLite (WAL mode) every 10 seconds, with 30-day retention.
- Dashboard tabs: Overview, Cooling, NVMe, Explorer, Events, Settings. Readings update in place, not by replacing the page. History graph supports flexible time ranges.
- Sensor identities are **sensor IDs**, not ambiguous names, with local aliases and favorite sensor selection.
- Fans: radiator bank = \`/lpc/it8688e/0/fan/0\`; DDC pump *tach only* = \`/lpc/it8688e/0/fan/1\`; NVMe heatsink fan = \`/lpc/it8688e/0/fan/2\`.
- Displays RPM and reported control percentage; **does not control any fans**. Continue using Fan Control for curves.
- Data remains on this computer under \`%LOCALAPPDATA%\SensorDash\` (\`telemetry.db\`, \`settings.json\`).
- HTTP binds to loopback only. No external services, accounts, or telemetry uploads.

## Sensor discovery and renaming
- Sensor readings update in place without resetting the Sensor Explorer's search, scroll position or alias editor.
- Select any sensor in **Sensor Explorer** to change its displayed name; click **Save name**. This is independent of whether it is used by a dashboard gauge.
- Open **Settings → Sensor role assignments** to map CPU temperature, GPU hotspot, NVMe composite, ALL RADS, pump tach and other dashboard roles to the precise device/sensor IDs available on your machine.
- **NVMe Cooling → Storage sensor diagnostics** shows your detected storage hardware, the number of temperature sensors, their IDs and values, the active polling interval and whether SensorDash has administrator privileges.
- If no SSD temperature is discovered, the library may lack the necessary device access. Close the ordinary SensorDash instance and **run the executable as administrator once for comparison**. If sensors appear only when elevated, automatic non-elevated startup will still lack them. Keep the web host local-only; SensorDash does not require or enforce elevation.
- **Storage polling is enabled** in the embedded collector and defaults to every 10 seconds. Fan Control keeps its own independent storage polling setting.

## Chart hover and elevated startup (v0.1.2)

Graph crosshairs and tooltips now render on their **own transparent overlay** and are cleared before each mouse move, preventing trails or ghosted hover markers.

Some storage/NVMe sensors need Windows administrator privileges. If NVMe temperature appears only when you run SensorDash as administrator, you can opt in to elevated startup:

1. Extract the full release into a permanent folder and close any currently running SensorDash process.
2. Double-click **Install-ElevatedStartup.cmd**, approve the **one-time UAC prompt**, and read the output.
3. At your next sign-in, Task Scheduler launches SensorDash with highest privileges **as your interactive Windows account**, not as SYSTEM, with no repeated UAC prompts.
4. To reverse this, double-click **Remove-ElevatedStartup.cmd**, then **Install-Startup.cmd** if you want ordinary startup again.

The elevated installer removes the standard startup shortcut **only after successful task registration**, preventing duplicate startup. Regular startup does not grant NVMe privileges. Neither mode changes Fan Control.

**Security trade-off:** Elevated startup also elevates the locally hosted dashboard/API. SensorDash listens exclusively on \`127.0.0.1\`, but other programs on the same computer can reach that local API. Use the optional elevated task only if you want the additional sensor access and understand this trade-off. It cannot be installed silently; Windows administrator approval is required. If your everyday Windows account is not an administrator and UAC uses a different account, use ordinary startup until an appropriate per-user privilege arrangement is configured.

## Performance improvements (v0.1.3)

Sensor collection and screen rendering are intentionally separate:

- Hardware values continue polling and are written to history at the configured rate.
- Live numbers update using coalesced animation-frame callbacks, and only changed text is written to the page.
- Charts are refreshed at most every **8 seconds**, independent from the sensor polling interval.
- **While your mouse is over a graph, SensorDash defers graph repainting** so hovering across samples stays uninterrupted. The graph catches up after you move away.
- Historical data is refreshed independently every 30 seconds, so long-running sessions still have complete timelines.
- Charts avoid needless canvas-buffer resize operations, which previously reset the hover context and caused hitches.
- If your computer is under heavy load, incoming sensor data continues recording without requiring an immediate graph paint.

Fan Control (v238 and later) uses a PawnIO-based LibreHardwareMonitor backend and can use elevated scheduled startup or its newer Windows service mode. The current user's service/task privilege level depends on how Fan Control is configured. SensorDash keeps fan control read-only and independent.

## Development
Install .NET 10 SDK. From the repo:

\`\`\`powershell
dotnet run --project src/SensorDash/SensorDash.csproj
\`\`\`

For a portable Windows build:

\`\`\`powershell
dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
\`\`\`

The Actions workflow automatically builds an x64 self-contained Windows executable and uploads a ZIP artifact on commits and pull requests. No signing certificate is configured, so Windows SmartScreen may initially warn for an unsigned app.

## Notes
- Many sensors are only accessible with elevated privileges and some sensors conflict with other readers. Test alongside Fan Control; avoid allowing two apps to write fan controls.
- Dashboard history is recorded only while SensorDash is running; the app does not import the old CSV logs in this first release.
- Current implementation targets Windows x64. Browser and service run locally on the same machine.
- GPU hotspot and SSD composite temperatures are shown separately. No coolant-temperature reading is synthesized.
- MPL-2.0 notices apply to the LibreHardwareMonitorLib dependency; SensorDash's own license is Apache-2.0. Preserve third-party license notices when distributing.
