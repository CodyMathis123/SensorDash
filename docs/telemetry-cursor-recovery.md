# Recovering corrupt telemetry export checkpoints

SensorDash writes three read-only export checkpoint files under
`%ProgramData%\SensorDash`:

| Checkpoint | Position |
| --- | --- |
| `telemetry-export-cursor.json` | Last acknowledged sensor `(timestamp, sensor)` |
| `telemetry-accessory-cursor.json` | Last acknowledged accessory power `(timestamp, system, component)` |
| `telemetry-event-cursor.json` | Last acknowledged threshold event ID |

The exporter is independent of hardware collection, local history, and fan/PWM
control. It saves a checkpoint only after its respective HTTPS batch is
acknowledged by the receiver.

## Persistence and automatic recovery

- Every acknowledged checkpoint write creates/replaces both `*.json` and
  `*.json.bak` through separately flushed temporary files and atomic
  same-directory rename. The backup can be at most one acknowledged batch
  behind after a write interruption.
- When a valid primary exists, a missing or invalid backup is rebuilt.
- When the primary contains invalid JSON, NUL bytes, or an invalid position,
  SensorDash preserves it as `*.corrupt-<UTC timestamp>-<suffix>`,
  restores the last validated backup, and replays any already-acknowledged
  records after that backup if necessary.
- If **both** copies are invalid or unavailable, SensorDash preserves damaged
  files, resets the checkpoint to the *beginning of locally retained SQLite
  history*, and resends available records. This prioritizes not losing any
  **retained unsent** measurements over avoiding a harmless retransmission.
  Samples already pruned by SQLite retention cannot be reconstructed.
- When no primary or backup exists (genuinely first-time setup), the numeric
  and accessory exporters still initialize to the newest archived timestamp;
  they do not automatically upload old history. Threshold events retain their
  existing initial position of zero.

Automatic recovery warnings identify the checkpoint family (sensor, accessory,
or event), whether a backup or archive replay was used, and any limitation,
without logging token bytes, configuration contents, sensor IDs, or disk paths.
No restart is required; the exporter initializes again in its existing retry
loop. Filesystem/SQLite I/O failures still retry rather than discarding state.

### Checking success

Open **SensorDash → Settings → Pi telemetry export**. The `Exporter` state
should be `running`, the `last delivery` timestamp should become recent,
and `retry reason` should clear. You can also read the local
`GET http://localhost:8765/api/telemetry-config` endpoint and inspect
`runtimeState`, `lastDeliveredUtc`, and `lastFailureClass`.

A recovery warning in Windows Event Viewer remains valuable evidence. Preserve
the quarantined files if investigating disk or unexpected-power-loss behavior;
do not delete the local SQLite archive.

### Testing

The compiled `--self-test` suite exercises the actual NUL-byte scenario,
truncated data, semantically invalid JSON, missing primary, absent backup,
double corruption, fresh installs, and all three checkpoint types using real
filesystem writes and the SQLite telemetry reader. Windows CI runs the suite
before assembling the MSI.

**Scope:** This prevents permanently wedged exporter initialization caused by
checkpoint corruption; it does not identify the underlying cause of the
zero-filled files or repair an unhealthy disk.
