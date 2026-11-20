using Microsoft.Data.Sqlite;

// Hardware-free integration checks. No fan/PWM objects or Windows service
// startup are needed to prove archival, queue overflow and UI isolation.
internal static class ArchiveIsolationSelfTest
{
    internal static void Run()
    {
        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Archive isolation: " + message);
        }

        var directory = Path.Combine(Path.GetTempPath(), "SensorDash-archive-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var settings = new DashboardSettings
            {
                HistoryMode = "all",
                SaveEverySeconds = 10,
                HighThresholds = new() { ["cpu-temp"] = 70 }
            };
            var now = DateTimeOffset.UtcNow;
            SensorFrame Make(double value, int seconds) =>
                new(now.AddSeconds(seconds), [
                    new SensorReading("cpu-temp", "CPU Temperature", "CPU",
                        "Cpu", "Temperature", value, now.AddSeconds(seconds))
                ], null);

            var queue = new ArchiveQueue(2);
            Check(queue.TryEnqueue(Make(80, 0), settings, true, []), "first queued archive frame");
            Check(queue.TryEnqueue(Make(81, 10), settings, true, []), "second queued archive frame");
            Check(!queue.TryEnqueue(Make(82, 20), settings, true, []),
                "overflow must be nonblocking");
            Check(queue.Dropped == 1 && queue.Accepted == 2,
                "overload must be observable and bounded");

            var live = new DashboardState(directory);
            var nonJson = new SensorFrame(now, [
                new SensorReading("invalid", "Synthetic non-JSON finite check",
                    "Test", "Cpu", "Temperature", double.NaN, now)
            ], null);
            live.Publish(nonJson);
            Check(ReferenceEquals(live.Latest, nonJson),
                "publishing Latest must not perform JSON serialization");
            live.Publish(Make(60, 30));
            Check(live.PendingLiveFrames.TryRead(out var coalesced) &&
                  coalesced.Timestamp == now.AddSeconds(30) &&
                  !live.PendingLiveFrames.TryRead(out _),
                "slow UI consumers must receive the latest, not an old backlog");

            var db = new HistoryStore(Path.Combine(directory, "history.db"));
            var processor = new ArchiveProcessor(db);
            var begin = Make(80, 0);
            var first = new ArchiveWork(begin, settings, true, [
                new FanRecoveryEvent("warning", "fan:profile:demo", "Test guard transition", null)
            ]);
            processor.Process(first);
            processor.Process(first); // Previously committed stages must not duplicate.
            processor.Process(new ArchiveWork(Make(81, 11), settings, true, []));
            processor.Process(new ArchiveWork(Make(60, 22), settings, true, []));
            using var connection = new SqliteConnection(
                $"Data Source={Path.Combine(directory, "history.db")};Pooling=False");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM samples";
            Check(Convert.ToInt64(cmd.ExecuteScalar()) == 3, "ordered sample archival");
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE severity='warning' AND sensor='cpu-temp'";
            Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "threshold crossing exactly once");
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE severity='info' AND sensor='cpu-temp'";
            Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "threshold recovery exactly once");
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE sensor='fan:profile:demo'";
            Check(Convert.ToInt64(cmd.ExecuteScalar()) == 1, "cooling diagnostic archived once");
            cmd.CommandText = "PRAGMA integrity_check";
            Check((string?)cmd.ExecuteScalar() == "ok", "archive SQLite integrity");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
