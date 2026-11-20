using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal readonly record struct HistoryCleanupBatch(int Samples, int Events, int AccessoryPower)
{
    internal int Total => Samples + Events + AccessoryPower;
}

public sealed partial class HistoryStore
{
    // Retention uses a separate connection and small write transactions, never
    // the sensor/fan-control worker. No one transaction may erase millions of
    // rows or hold a long-lived write lock while PWM needs the collector.
    internal HistoryCleanupBatch CleanupBatch(int retentionDays, int batchSize = 2048)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 1095))
            .ToUnixTimeMilliseconds();
        var limit = Math.Clamp(batchSize, 1, 4096);
        using var connection = Open();
        connection.DefaultTimeout = 1;
        using (var busy = connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=25";
            busy.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        // rowid batching works for the ordinary rowid tables in our schema.
        // With export enabled, SQLite uses the existing (ts,sensor) index;
        // without it the rowid table remains usable, without adding a
        // blocking index migration during active hardware collection.
        int Delete(string sql, int maxRows)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 1;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$before", cutoff);
            command.Parameters.AddWithValue("$limit", maxRows);
            return command.ExecuteNonQuery();
        }

        var samples = Delete("""
            DELETE FROM samples WHERE rowid IN
                (SELECT rowid FROM samples WHERE ts < $before LIMIT $limit)
            """, limit);
        var accessories = Delete("""
            DELETE FROM accessory_power WHERE rowid IN
                (SELECT rowid FROM accessory_power WHERE ts < $before LIMIT $limit)
            """, Math.Min(limit, 1024));
        var events = Delete("""
            DELETE FROM events WHERE rowid IN
                (SELECT rowid FROM events WHERE ts < $before LIMIT $limit)
            """, Math.Min(limit, 256));
        transaction.Commit();
        return new HistoryCleanupBatch(samples, events, accessories);
    }

    internal void TryRetentionCheckpoint()
    {
        // Best effort and non-waiting: readers/exporter may have SQLite open.
        // FULL auto_vacuum reclaims database pages at a successful checkpoint.
        try
        {
            using var connection = Open();
            connection.DefaultTimeout = 1;
            using var command = connection.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "PRAGMA busy_timeout=0; PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6)
        {
            // Do not compete with a live collector or exporter; retry later.
        }
    }
}

internal static class HistoryRetentionSchedule
{
    internal const int BatchSize = 2048;
    internal static readonly TimeSpan BetweenBatches = TimeSpan.FromMilliseconds(100);
    internal static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(10);
}

internal sealed class HistoryRetentionWorker(
    DashboardState state, HistoryStore store, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _events = loggerFactory.CreateLogger(OperationalEvents.Category);
    private readonly OperationalOutage _outage = new(
        loggerFactory.CreateLogger(OperationalEvents.Category),
        "local SQLite retention", OperationalEvents.RetentionFailure,
        OperationalEvents.RetentionFailureSummary, OperationalEvents.RetentionRecovered);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // SensorWorker gets an opportunity to initialize first. This worker
        // never opens hardware, evaluates fan profiles, or blocks on a large purge.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var currentRetention = -1;
        var nextSweep = DateTimeOffset.MinValue;
        var sweepStarted = DateTimeOffset.UtcNow;
        long totalSamples = 0, totalEvents = 0, totalAccessories = 0;
        var batchesSinceCheckpoint = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var retention = Math.Clamp(state.Settings.RetentionDays, 1, 1095);
            var now = DateTimeOffset.UtcNow;
            if (retention != currentRetention)
            {
                currentRetention = retention;
                nextSweep = DateTimeOffset.MinValue; // React promptly to Settings changes.
                sweepStarted = now;
                totalSamples = totalEvents = totalAccessories = 0;
                batchesSinceCheckpoint = 0;
            }

            if (now < nextSweep)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var result = store.CleanupBatch(retention, HistoryRetentionSchedule.BatchSize);
                _outage.Recovered();
                totalSamples += result.Samples;
                totalEvents += result.Events;
                totalAccessories += result.AccessoryPower;

                if (result.Total > 0)
                {
                    if (++batchesSinceCheckpoint >= 128)
                    {
                        store.TryRetentionCheckpoint();
                        batchesSinceCheckpoint = 0;
                    }
                    await Task.Delay(HistoryRetentionSchedule.BetweenBatches, stoppingToken);
                    continue;
                }

                store.TryRetentionCheckpoint();
                if (totalSamples + totalEvents + totalAccessories > 0)
                    _events.LogInformation(OperationalEvents.RetentionCompleted,
                        "Local SQLite retention cleanup complete: {Days} days, {Samples} samples, {Events} events, {AccessoryPower} accessory readings removed over {Seconds:0.0}s.",
                        retention, totalSamples, totalEvents, totalAccessories,
                        (DateTimeOffset.UtcNow - sweepStarted).TotalSeconds);

                totalSamples = totalEvents = totalAccessories = 0;
                nextSweep = DateTimeOffset.UtcNow.Add(HistoryRetentionSchedule.IdleSweepInterval);
                sweepStarted = DateTimeOffset.UtcNow;
            }
            catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6)
            {
                // A competing writer is expected. Yield without logging noise.
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                _outage.Failed(e, "retention-batch");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
