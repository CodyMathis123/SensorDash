# Collector startup and crash diagnostics

The elevated `SensorDashCollector` service runs the dashboard and PWM controller.
A crash in a third-party hardware library can occur on a library-created thread,
outside `SensorWorker`'s exception handler. On 2026-11-17, Windows recorded a
DiskInfoToolkit `NullReferenceException` during SMART storage discovery followed
by a SensorDash process crash (Application 1026/1000) and SCM event 7034.

## Collector health in the dashboard

Open **Settings → Collector health** in the local dashboard. This read-only
panel displays current process uptime, unexpected terminations reported by
Service Control Manager (SCM) in the previous seven days, the latest unexpected
termination, and the latest service start within 15 minutes of that crash.
Expand **Recent service events** to see SCM event IDs 7031, 7034 and 7036.

**Important:** An SCM start after a crash is not proof that Windows performed
an automatic recovery; a person could have started the service manually.
The UI intentionally labels it **Last start after crash**, rather than
"Last automatic recovery". No unexpected terminations means none were found
in the scanned System log window, not a guarantee that no crash ever occurred.

The endpoint is `GET /api/collector-health`, available only on loopback.
It reads at most 512 recent relevant SCM records, returns at most 12 sanitized
service events, and does not expose arbitrary log messages or settings secrets.
Windows event-log access errors are displayed as **Unavailable**, not zero.
The browser refreshes the panel while Settings is open and offers a manual
refresh button; this service-hosted dashboard cannot detect a collector that
is currently down. An independent Windows monitor is needed for that.

## Diagnosis

Run an elevated PowerShell terminal:

```powershell
.\scripts\Get-SensorDashDiagnostics.ps1 -Days 7
sc.exe qc SensorDashCollector
sc.exe qfailure SensorDashCollector
```

The diagnostic ZIP includes selected Windows service/crash events, non-secret
service configuration, and the collector startup log under
`%ProgramData%\SensorDash\Logs\collector-startup.log`. Review before sharing:
Windows crash events may include paths and device details. The startup log is
bounded to roughly 2 MiB plus one previous generation and is best-effort.
A native access violation may kill the process without invoking managed handlers;
Windows Application Error/Windows Error Reporting remain authoritative.

The MSI configures SCM restart recovery at 60 seconds after unexpected crashes.
This does not fix the underlying DiskInfoToolkit bug. Confirm using
`sc.exe qfailure SensorDashCollector` after installing a newly built MSI.
Existing installations remain unchanged until the MSI upgrade is installed.
Normal stop/uninstall must not trigger a restart.

## Storage discovery mitigation

Storage collection remains enabled by default. If DiskInfoToolkit continues
to crash on startup, use SensorDash Settings to disable the **Storage** hardware
collection family while leaving CPU, GPU, motherboard/Super I/O and cooling
controllers enabled. This prevents storage discovery during subsequent
`Computer.Open()` calls, but temporarily removes disk SMART telemetry.

Alternatively, for a service-wide emergency opt-out, set the machine environment
variable `SENSORDASH_DISABLE_STORAGE=1` and restart the collector during a
supervised cooling-maintenance window. Because Windows services receive machine
environment variables at process creation, reboot if the setting is not observed.
Remove the variable and restart to restore storage discovery. Do not assume
the opt-out protects an already-running process.

## Follow-up investigation

Investigate the exact device and dependency version causing
`DiskInfoToolkit.Smart.SmartAttributeHandler.CheckSmartAttributeCorrect`
to throw on the `DevicesChangedListener` thread. Wrapping only the
SensorDash collection loop cannot catch that exception. Until the dependency
is fixed or storage enumeration is isolated in another process, service recovery
is a mitigation, not fault containment. Do not introduce duplicate sensor
collectors or unsafe PWM initialization on restart.
