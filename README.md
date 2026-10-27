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
- Dashboard tabs: Overview, Cooling, NVMe, Explorer, Events, Settings. History graph supports flexible time ranges.
- Sensor identities are **sensor IDs**, not ambiguous names, with local aliases and favorite sensor selection.
- Fans: radiator bank = \`/lpc/it8688e/0/fan/0\`; DDC pump *tach only* = \`/lpc/it8688e/0/fan/1\`; NVMe heatsink fan = \`/lpc/it8688e/0/fan/2\`.
- Displays RPM and reported control percentage; **does not control any fans**. Continue using Fan Control for curves.
- Data remains on this computer under \`%LOCALAPPDATA%\SensorDash\` (\`telemetry.db\`, \`settings.json\`).
- HTTP binds to loopback only. No external services, accounts, or telemetry uploads.

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
