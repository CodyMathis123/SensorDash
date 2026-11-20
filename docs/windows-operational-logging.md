# SensorDash Windows operational logging

SensorDash's **installed collector** writes actionable events to:

- Event Viewer → **Applications and Services Logs → SensorDash**
- Log name: `SensorDash`
- Provider/source: `SensorDash.Collector`

The MSI registers this source in the separate classic Windows event log
before starting the Windows service. No migration of older entries in
**Windows Logs → Application** is attempted. .NET Runtime / Application Error,
Windows Error Reporting, Service Control Manager, and MSI installer events
remain in Windows' own Application or System logs.

The native tray makes read-only requests to `/api/tray-state` roughly every
two seconds but emits **no** Event Log entries itself. The collector's
successful HTTP requests, ASP.NET Core endpoint routing, and ordinary worker
Information messages are excluded from the operational log. Framework
warnings/errors remain eligible for the log. Debug and Trace are not written
to Windows Event Viewer.

## Level policy

| Level | Event Log policy |
| --- | --- |
| Critical / Error | Service failures and nonrecoverable problems, plus framework errors |
| Warning | First observed degradation and occasional continuing-outage summaries |
| Information | Explicit service lifecycle and completed recovery events only |
| Debug / Trace | No Windows event; detailed fan/PWM tracing remains in the bounded `logs/fan-control.log` file |

## Stable operational event IDs

| Range | Meaning |
| --- | --- |
| 1000 / 1001 | Collector started / normal shutdown |
| 2001 / 2002 / 2003 | Hardware collection failed / still failing / recovered |
| 2004 / 2005 | Resume-triggered LHM reopen requested / completed and sensor scan succeeded |
| 2006 / 2007 / 2008 | Windows resume callback registration failure / hardware close warning / accessory history failure |
| 3001 | Cooling event history archive warning |
| 3002 / 3003 | Cooling safety guard/failsafe entered / cleared (one event per state transition) |
| 4001 / 4002 / 4003 | Exporter failure / continuing outage / recovery (subsystem identifies initialization, delivery, or catalog sync) |
| 4004 | Outbound telemetry configuration reloaded |

Exporter and sensor collection outages use the same bounded policy:
- Log the first failure with its subsystem, safe stage identifier, and exception class.
- Count all retries without writing each retry to the Event Log.
- If the outage persists, write at most one continuing-failure summary per 15 minutes.
- When actually recovered, log the outage duration and failed attempt count.
- Disabling export resets exporter outage episodes without a false recovery.
- The exporter labels initialization, HTTPS delivery, and catalog synchronization
  separately. Recovery from delivery failure requires a completed iteration
  with a successful receiver acknowledgement; merely reading settings does not
  count as successful delivery.

No telemetry tokens, configuration JSON, sensor IDs, or untrusted remote response
bodies are copied into these operational messages. Exception class names are
recorded; routine retry stack traces are intentionally omitted.

The LHM resume event `2004` means the reopen **started**. Event `2005`
means the reopened hardware was scanned and the approved control profiles were
evaluated on that collection cycle. It does **not** guarantee a measured PWM
signal or external fan RPM; the existing fan-control trace and feedback
monitoring remain authoritative for that distinction.

## Read events with PowerShell

```powershell
Get-WinEvent -FilterHashtable @{
  LogName = 'SensorDash'
  StartTime = (Get-Date).AddDays(-1)
} | Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message
```

```powershell
Get-WinEvent -FilterHashtable @{LogName='SensorDash';Level=2,3} |
  Group-Object Id, LevelDisplayName | Sort-Object Count -Descending |
  Select-Object Count, Name
```

`EventId=0` and similar IDs may still occur in Windows-owned framework
warnings/errors. SensorDash-authored operational events use IDs above.

## Validation

The executable's `--self-test` exercises log-level filtering and the
15-minute first/summary/recovered state machine with a synthetic clock. The
Windows MSI CI validates source registration, the log channel, collector
startup event ID 1000, quiet repeated tray HTTP requests, and existing MSI
upgrade/repair/uninstall behavior.

Local sensor sampling and approved PWM outputs do not depend on successful
Event Log writes. This is an observational feature.
