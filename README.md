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
