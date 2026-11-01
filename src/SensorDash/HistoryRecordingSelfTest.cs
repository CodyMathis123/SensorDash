using Microsoft.Data.Sqlite;

// Hardware-free archive regression tests. No Windows fan or hardware access.
internal static class HistoryRecordingSelfTest
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SensorDash-cadence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        try
        {
            var archive = new HistoryStore(path);
            var config = new DashboardSettings
            {
                HistoryMode = "all", SaveEverySeconds = 10,
                StorageHistorySeconds = 60, NetworkHistorySeconds = 60
            };
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-25);
            SensorReading Cpu(int n) => new("/cpu/0/temperature/0", "CPU Package", "CPU", "Cpu",
                "Temperature", 40 + n);
            SensorReading Network(int n) => new("/network/0/throughput/0", "Data Uploaded", "Ethernet", "Network",
                "Throughput", n * 2);
            SensorReading Storage(int n) => new("/storage/0/temperature/0", "NVMe Temperature", "NVMe", "Storage",
                "Temperature", 40 + n,
                t0.AddSeconds((n / 30) * 30)); // LHM storage poll cache, every 30 sec.
            long Count(string id)
            {
                using var c = new SqliteConnection($"Data Source={path};Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM samples WHERE sensor=$id";
                cmd.Parameters.AddWithValue("$id", id);
                return Convert.ToInt64(cmd.ExecuteScalar());
            }
            static void Check(bool okay, string reason)
            {
                if (!okay) throw new InvalidOperationException("Archive cadence regression: " + reason);
            }
            for (var sec = 0; sec <= 120; sec += 10)
                archive.Record(new SensorFrame(t0.AddSeconds(sec),
                    [Cpu(sec), Network(sec), Storage(sec)], null), config);
            Check(Count("/cpu/0/temperature/0") == 13,
                "CPU history should remain every 10 seconds");
            Check(Count("/network/0/throughput/0") == 3,
                "Network should archive every 60 seconds");
            Check(Count("/storage/0/temperature/0") == 3,
                "Storage should archive every 60 seconds");
            Check(HistoryRecordingPolicy.EffectiveIntervalSeconds(Network(0), config, []) == 60,
                "Network cadence default");

            // Explicit user pin and cooling/threshold references override the
            // family interval without altering the global fan-controller loop.
            config.PinnedSensors = ["/network/0/throughput/0"];
            for (var sec = 130; sec <= 150; sec += 10)
                archive.Record(new SensorFrame(t0.AddSeconds(sec),
                    [Cpu(sec), Network(sec), Storage(sec)], null), config);
            Check(Count("/network/0/throughput/0") == 6,
                "Pinned NIC should use the important sensor interval");
            Check(Count("/cpu/0/temperature/0") == 16, "CPU cadence must remain unchanged");
            Check(Count("/storage/0/temperature/0") == 3,
                "Cached storage polling must not create synthetic history points");

            // Fresh observations can still be throttled independently; changing
            // a policy takes effect at the next eligible history write.
            config.StorageHistorySeconds = 10;
            for (var sec = 160; sec <= 210; sec += 10)
                archive.Record(new SensorFrame(t0.AddSeconds(sec),
                    [Cpu(sec), Network(sec), Storage(sec)], null), config);
            Check(Count("/storage/0/temperature/0") == 5,
                "Fast storage recording must not duplicate observations between 30s hardware polls");
            Check(Count("/cpu/0/temperature/0") == 22,
                "Reconfiguring family history should never affect CPU history");
            Check(Count("/network/0/throughput/0") == 12,
                "Pinned network collection should be independent of storage schedule");

            config.PinnedSensors.Clear();
            config.NetworkHistorySeconds = 300;
            archive.Record(new SensorFrame(t0.AddSeconds(220),
                [Cpu(220), Network(220), Storage(220)], null), config);
            Check(Count("/network/0/throughput/0") == 12,
                "Slowing Network history must immediately stop unwanted writes");

            // Global save interval is the floor; family settings cannot cause
            // extra writes between regular SQLite commits.
            config.SaveEverySeconds = 90;
            config.NetworkHistorySeconds = 10;
            Check(HistoryRecordingPolicy.EffectiveIntervalSeconds(Network(0), config, []) == 90,
                "Family cadence cannot exceed global recording frequency");
            Check(HistoryRecordingPolicy.EffectiveIntervalSeconds(Network(0), config,
                ["/network/0/throughput/0"]) == 90,
                "Important sensors follow global recording cadence");

            // The exporter reads the same rows/timestamps; no cursor or table
            // schema changed. Future samples remain ordered by their frame time.
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM samples WHERE ts > $max";
                cmd.Parameters.AddWithValue("$max", t0.AddSeconds(220).ToUnixTimeMilliseconds());
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 0,
                    "Archive timestamp must never be shifted into the future");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
