# Collector startup and crash diagnostics

The elevated `SensorDashCollector` service runs the dashboard and PWM controller.
A crash in a third-party hardware library can occur on a library-created thread,
outside `SensorWorker`'s exception handler. On 2026-11-17, Windows recorded a
DiskInfoToolkit `NullReferenceException` during SMART storage discovery followed
by a SensorDash process crash (Application 1026/1000) and SCM event 7034.

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
