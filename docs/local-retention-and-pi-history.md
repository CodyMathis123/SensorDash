# Local SQLite retention versus VictoriaMetrics history

## Storage ownership

- **SensorDash (Windows)** writes raw sensor measurements, cooling-derived accessory
  power, and local events to `%ProgramData%\SensorDash\telemetry.db`. The same
  SQLite database is its durable outbound retry spool. It is not the long-term
  monitoring datastore.
- **HomeAwayFromHome (Raspberry Pi)** receives acknowledged HTTPS exports and
  stores them in VictoriaMetrics. Its retention is independently configured
  in the Pi's `compose.yaml` (currently `-retentionPeriod=90d`).
- Deleting an expired Windows SQLite reading does not delete a previously
  ingested VictoriaMetrics reading. Conversely, an unsent Windows reading
  discarded by retention cannot be recovered from the Pi.

## Local retention policy

New installations default to **3 days** of SQLite history. Existing saved
Settings values are intentionally respected on upgrade: to change an existing
installation, open SensorDash Settings and change **Local retry-buffer retention
(days)** to `3`. The API setting is `retentionDays`.

The number of recorded sensors is controlled separately by `historyMode` and
history intervals. Changing `historyMode` to `balanced` or `in-use` also
reduces the metrics available to the Pi; shortening `retentionDays` does
not reduce the series transmitted during normal connectivity.

The local setting accepts 1 to 1095 days. A one-day buffer saves more disk
but loses data during outages exceeding a day. Three days provides a bounded
retry window while the Pi keeps longer-term history.

## Background maintenance

The `HistoryRetentionWorker` is a separate hosted service from
`SensorWorker`. It never opens hardware or evaluates fan/PWM state.

- It removes at most 2,048 expired sample rows per transaction, plus separately
  bounded expired accessory-power and event rows.
- It yields between batches and uses short SQLite lock waits. When another
  writer holds SQLite, it retries rather than blocking a fan-control pass.
- It catches up progressively after retention is reduced, then checks for new
  expired rows roughly every ten minutes.
- SQLite FULL auto-vacuum was already enabled; nonblocking checkpoint attempts
  periodically reclaim space. Large one-time deletions can take many batches
  and the on-disk size may shrink gradually.

The file size is not a direct measure of export backlog. Check
`/api/telemetry-config` for last acknowledged delivery, and compare the
`telemetry-export-cursor.json` timestamp with current measurements before
aggressively shortening the retention window.

All retention maintenance is best effort. Failure or SQLite contention is
reported using deduplicated operational events (IDs 5001-5004), not repeated
warnings on every retry. Existing detailed hardware polling and PWM behavior
remain independent.
