using Microsoft.Data.Sqlite;

internal static class HistoryRetentionSelfTest
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SensorDash-retention-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.db");
        try
        {
            // Start with the old, non-vacuuming schema and real retained data.
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE samples(sensor TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL, PRIMARY KEY(sensor,ts));
                    WITH RECURSIVE rows(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM rows WHERE n<20000)
                    INSERT INTO samples SELECT printf('old-sensor-%040d',n),n,42.5 FROM rows;
                    INSERT INTO samples VALUES('retained',$now,27.5);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                command.ExecuteNonQuery();
            }
            var store = new HistoryStore(path);
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)"; command.ExecuteNonQuery();
                command.CommandText = "SELECT COUNT(*) FROM samples";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 20001, "Migration lost readings");
                command.CommandText = "PRAGMA auto_vacuum";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 1, "Auto reclamation not enabled");
            }
            var before = new FileInfo(path).Length;
            store.Cleanup(30);
            Check(new FileInfo(path).Length < before / 2, "Retention did not reclaim file space");
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM samples";
                Check(Convert.ToInt64(command.ExecuteScalar()) == 1, "Retention removed the wrong rows");
                command.CommandText = "SELECT value FROM samples WHERE sensor='retained'";
                Check(Convert.ToDouble(command.ExecuteScalar()) == 27.5, "Retained value changed");
                command.CommandText = "PRAGMA integrity_check";
                Check((string?)command.ExecuteScalar() == "ok", "Database integrity failed");
            }
            store.Cleanup(30);
            _ = new HistoryStore(path); // Reopening must not require another migration.
            CheckAccessories(path, store);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
    private static void CheckAccessories(string path, HistoryStore store)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-4).ToUnixTimeMilliseconds();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO samples VALUES($sensor,$ts,$value)";
            command.Parameters.Add("$sensor", SqliteType.Text);
            command.Parameters.Add("$ts", SqliteType.Integer);
            command.Parameters.Add("$value", SqliteType.Real);
            foreach (var offset in new[] { 0, 10000, 180000, 190000 })
            foreach (var (sensor, value) in new[] { ("watts", 5.0), ("pwm", 50.0), ("rpm", 2000.0) })
            {
                if (sensor == "watts" && offset >= 180000) continue;
                command.Parameters["$sensor"].Value = sensor;
                command.Parameters["$ts"].Value = start + offset;
                command.Parameters["$value"].Value = value;
                command.ExecuteNonQuery();
            }
        }
        var model = new CoolingElectricalConfig { Quantity = 19, WattsPerUnit = 2,
            PowerSensorId = "watts", ControlSensorId = "pwm", RpmSensorId = "rpm",
            LowDutyPercent = 0, HighDutyPercent = 100, MeasuredLowWatts = 0, MeasuredHighWatts = 20 };
        var settings = new DashboardSettings { SaveEverySeconds = 10, CoolingSystems = [new() {
            Name = "Loop", Components = [
                new() { Name = "Fans", Kind = "fan-group", Electrical = model },
                new() { Name = "Pump", Kind = "pump", SensorIds = ["rpm"],
                    Electrical = new() { WattsPerUnit = 18 } },
                new() { Name = "Unobserved", Kind = "pump", Electrical = new() { WattsPerUnit = 18 } }
            ] }] };
        var results = store.ReadAccessoryStatistics(settings, new Dictionary<string, string> { ["rpm"] = "Fan" }, 1);
        Check(results[0].Source == "Mixed estimate" && results[0].Stats.Max == 10,
            "Measured power must beat calibration; calibrated group watts must not be multiplied by fan count");
        Check(Math.Abs(results[0].Stats.EnergyWh!.Value - 150.0 / 3600) < 0.00001,
            "Accessory energy must exclude sleep gaps and use the best recorded evidence");
        Check(results[1].Source == "Rated estimate" && Math.Abs(results[1].Stats.EnergyWh!.Value - 0.1) < 0.00001,
            "Pump rating must be applied only across observed runtime");
        Check(results[2].Stats.EnergyWh == null && results[2].RatedWatts == 18,
            "A rating alone must not imply continuous operation");
        Check(AccessoryPowerModel.Estimate(new() { WattsPerUnit = 18 }, null, 0, null) == null,
            "PWM alone does not establish runtime or zero draw");
        var curveModel=settings.CoolingSystems[0].Components[1].Electrical!;
        curveModel.PowerCurve=[new() { Rpm=0,Watts=0 },new() { Rpm=5000,Watts=10 }];
        curveModel.Quantity=2;
        var curveHistory=store.ReadAccessoryStatistics(settings,new Dictionary<string,string> { ["rpm"]="Fan" },1)[1];
        Check(curveHistory.Source=="Power curve estimate" && curveHistory.Stats.EnergyWh>0,
            "Power curves feed accessory energy from recorded tachometer samples");
        Check(WorkspaceLogic.ReferencedSensorIds(settings).Contains("watts"), "Electrical sensors must be recorded in in-use mode");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
