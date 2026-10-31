using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

// These records are written at archive time, under the *then-current* cooling
// configuration. Replaying a network backlog never reinterprets old values
// using a newer PWM calibration or different component wiring.
internal sealed record ArchivedAccessoryPower(
    string System, string Component, long TimestampMs, double Watts,
    string Source, string Revision);

internal static class AccessoryPowerCapture
{
    internal static string Revision(CoolingComponentConfig component, string rpmId)
    {
        // e.g. changing 19 fans to 18, selected PWM sensor, or calibration
        // produces a different model ID. Names and aliases do not.
        var canonical = JsonSerializer.Serialize(new
        {
            component.Kind,
            rpmId,
            electrical = component.Electrical,
        }, JsonSerializerOptions.Web);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static List<ArchivedAccessoryPower> Capture(SensorFrame frame, DashboardSettings settings)
    {
        var rows = new List<ArchivedAccessoryPower>();
        if (!string.IsNullOrEmpty(frame.Error) || frame.Sensors.Count == 0)
            return rows;

        var index = frame.Sensors.Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        double? Get(string? sensorId, string kind)
        {
            if (string.IsNullOrWhiteSpace(sensorId) ||
                !index.TryGetValue(sensorId, out var sensor) ||
                sensor.Type != kind ||
                sensor.Value is not double value || !double.IsFinite(value))
                return null;
            // Never use an explicitly stale reading after sleep, sensor loss or
            // a device that hasn't been polled for >30s. No fabricated samples.
            if (sensor.ObservedAt is DateTimeOffset observed &&
                Math.Abs((frame.Timestamp - observed).TotalSeconds) > 30)
                return null;
            return value;
        }

        foreach (var system in (settings.CoolingSystems ?? []).Take(20))
        foreach (var component in (system.Components ?? []).Take(32))
        {
            if (component.Kind is not ("fan-group" or "pump") ||
                component.Electrical is not { } electrical)
                continue;
            var rpmId = !string.IsNullOrWhiteSpace(electrical.RpmSensorId)
                ? electrical.RpmSensorId
                : (component.SensorIds ?? []).FirstOrDefault(id =>
                    index.TryGetValue(id, out var sensor) && sensor.Type == "Fan") ?? "";
            var measured = Get(electrical.PowerSensorId, "Power");
            var duty = Get(electrical.ControlSensorId, "Control");
            var rpm = Get(rpmId, "Fan");
            var estimate = AccessoryPowerModel.Estimate(electrical, measured, duty, rpm);
            if (estimate is not { } result ||
                !double.IsFinite(result.Watts) || result.Watts < 0 ||
                result.Watts > 100000) continue;
            var provenance = result.Source switch
            {
                "Measured" => "measured",
                "Calibrated estimate" => "calibrated",
                "Power curve estimate" => "curve",
                "Rated estimate" => "rated",
                _ => "unknown",
            };
            if (provenance == "unknown") continue;
            rows.Add(new ArchivedAccessoryPower(
                system.Id, component.Id, frame.Timestamp.ToUnixTimeMilliseconds(),
                result.Watts, provenance, Revision(component, rpmId)));
        }
        return rows;
    }
}

public sealed partial class HistoryStore
{
    internal void RecordAccessoryPower(SensorFrame frame, DashboardSettings settings)
    {
        var rows = AccessoryPowerCapture.Capture(frame, settings);
        if (rows.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO accessory_power
                (system,component,ts,watts,source,revision)
            VALUES ($system,$component,$ts,$watts,$source,$revision)
            """;
        var system = command.Parameters.Add("$system", SqliteType.Text);
        var component = command.Parameters.Add("$component", SqliteType.Text);
        var ts = command.Parameters.Add("$ts", SqliteType.Integer);
        var watts = command.Parameters.Add("$watts", SqliteType.Real);
        var source = command.Parameters.Add("$source", SqliteType.Text);
        var revision = command.Parameters.Add("$revision", SqliteType.Text);
        foreach (var row in rows)
        {
            system.Value = row.System; component.Value = row.Component;
            ts.Value = row.TimestampMs; watts.Value = row.Watts;
            source.Value = row.Source; revision.Value = row.Revision;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    internal long LatestAccessoryPowerTimestamp()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(ts) FROM accessory_power";
        return command.ExecuteScalar() is long ts
            ? ts : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    internal List<ArchivedAccessoryPower> ReadAccessoryPowerBatch(
        long afterTimestamp, string afterSystem, string afterComponent, int requested = 256)
    {
        var result = new List<ArchivedAccessoryPower>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system,component,ts,watts,source,revision FROM accessory_power
            WHERE (ts,system,component) > ($ts,$system,$component)
            ORDER BY ts,system,component LIMIT $limit
            """;
        command.Parameters.AddWithValue("$ts", afterTimestamp);
        command.Parameters.AddWithValue("$system", afterSystem);
        command.Parameters.AddWithValue("$component", afterComponent);
        command.Parameters.AddWithValue("$limit", Math.Clamp(requested, 1, 512));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var watts = reader.GetDouble(3);
            if (!double.IsFinite(watts) || watts < 0) continue;
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                watts, reader.GetString(4), reader.GetString(5)));
        }
        return result;
    }
}

internal static class AccessoryPowerExportProtocol
{
    internal static string Serialize(IEnumerable<ArchivedAccessoryPower> rows, string host)
    {
        var output = new StringBuilder();
        foreach (var row in rows)
        {
            output.Append("sensordash_accessory_power,host=")
                .Append(TelemetryLineProtocol.Label(host, 80))
                .Append(",system=").Append(TelemetryLineProtocol.Label(row.System, 80))
                .Append(",component=").Append(TelemetryLineProtocol.Label(row.Component, 80))
                .Append(",source=").Append(row.Source)
                .Append(",revision=").Append(row.Revision)
                .Append(" value=").Append(row.Watts.ToString("G17", CultureInfo.InvariantCulture))
                .Append(' ').Append(row.TimestampMs.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return output.ToString();
    }
}

internal static class AccessoryPowerTelemetrySelfTest
{
    internal static void Run()
    {
        static void Assert(bool value, string description)
        {
            if (!value) throw new InvalidOperationException("Accessory telemetry self-test: " + description);
        }
        const long ts = 1793450400000;
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(ts);
        var settings = new DashboardSettings
        {
            CoolingSystems =
            [
                new CoolingSystemConfig
                {
                    Id = "loop-1", Name = "Liquid Loop", Kind = "liquid",
                    Components =
                    [
                        new CoolingComponentConfig
                        {
                            Id = "fan-bank", Name = "Radiator Fans", Kind = "fan-group",
                            Electrical = new CoolingElectricalConfig
                            {
                                Quantity = 19, Volts = 12, WattsPerUnit = 1.0,
                                RpmSensorId = "fan-rpm", ControlSensorId = "fan-pwm",
                                PowerSensorId = "fan-watts",
                                LowDutyPercent = 10, HighDutyPercent = 100,
                                MeasuredLowWatts = 2.5, MeasuredHighWatts = 24,
                            }
                        },
                        new CoolingComponentConfig
                        {
                            Id = "ddc", Name = "DDC Pump", Kind = "pump",
                            Electrical = new CoolingElectricalConfig
                            {
                                Quantity = 1, WattsPerUnit = 18, RpmSensorId = "pump-rpm",
                            }
                        }
                    ],
                }
            ]
        };
        SensorReading R(string id, string type, double? value, DateTimeOffset? at = null) =>
            new(id, id, "Test", "Test", type, value, at ?? stamp);
        var frame = new SensorFrame(stamp,
        [
            R("fan-rpm", "Fan", 1100), R("fan-pwm", "Control", 50),
            R("fan-watts", "Power", 12), R("pump-rpm", "Fan", 4000),
        ], null);
        var data = AccessoryPowerCapture.Capture(frame, settings);
        Assert(data.Count == 2, "fan group and pump represented");
        Assert(data[0].Watts == 12 && data[0].Source == "measured", "measured group draw takes precedence");
        Assert(data[1].Watts == 18 && data[1].Source == "rated", "rated pump requires observed RPM");
        Assert(data[0].Revision.Length == 16 && data[0].Revision.All(Uri.IsHexDigit),
            "stable version hash");
        var originalRevision = data[0].Revision;
        settings.CoolingSystems[0].Components[0].Name = "Renamed fans";
        Assert(AccessoryPowerCapture.Capture(frame, settings)[0].Revision == originalRevision,
            "renaming components preserves series revision");
        settings.CoolingSystems[0].Components[0].Electrical!.Quantity = 18;
        Assert(AccessoryPowerCapture.Capture(frame, settings)[0].Revision != originalRevision,
            "fan-count change creates new version");
        var e = settings.CoolingSystems[0].Components[0].Electrical!;
        e.Quantity = 19;
        Assert(AccessoryPowerCapture.Capture(frame, settings)[0].Revision == originalRevision,
            "original calibration restores stable hash");
        frame = frame with { Sensors =
        [
            R("fan-rpm", "Fan", 1100), R("fan-pwm", "Control", 50),
            R("pump-rpm", "Fan", 0),
        ] };
        data = AccessoryPowerCapture.Capture(frame, settings);
        Assert(data[0].Source == "calibrated" &&
            Math.Abs(data[0].Watts - (2.5 + (24.0 - 2.5) * 40.0 / 90.0)) < 1e-6,
            "group calibration stays unmultiplied by 19 fan count");
        Assert(data[1].Watts == 0 && data[1].Source == "rated",
            "explicit stopped RPM reports zero rated power");
        e.LowDutyPercent = null; e.HighDutyPercent = null;
        Assert(AccessoryPowerCapture.Capture(frame, settings).Count == 1,
            "fan draw unknown without measured watts, calibration, curve or active RPM");
        frame = frame with { Sensors = [R("pump-rpm", "Fan", 4200, stamp.AddMinutes(-3))] };
        Assert(AccessoryPowerCapture.Capture(frame, settings).Count == 0,
            "stale tachometer never becomes an inferred power reading");
        Assert(AccessoryPowerCapture.Capture(new SensorFrame(stamp, [], "offline"), settings).Count == 0,
            "no samples during collection failure");

        var root = Path.Combine(Path.GetTempPath(), "SensorDash-accessory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = new HistoryStore(Path.Combine(root, "archive.db"));
            e.LowDutyPercent = 10; e.HighDutyPercent = 100;
            frame = frame with { Sensors =
            [
                R("fan-rpm", "Fan", 1100), R("fan-pwm", "Control", 50),
                R("fan-watts", "Power", 12), R("pump-rpm", "Fan", 4000),
            ] };
            db.RecordAccessoryPower(frame, settings);
            var entries = db.ReadAccessoryPowerBatch(0, "", "", 1);
            Assert(entries.Count == 1 && entries[0].Component == "ddc" &&
                entries[0].Source == "rated", "ordered SQLite cursor first component");
            var rest = db.ReadAccessoryPowerBatch(entries[0].TimestampMs,
                entries[0].System, entries[0].Component);
            Assert(rest.Count == 1 && rest[0].Component == "fan-bank",
                "cursor preserves peers sharing timestamps");
            Assert(AccessoryPowerExportProtocol.Serialize(rest, "PC").Contains(
                ",source=measured,revision=" + rest[0].Revision +
                " value=12 1793450400000"), "line protocol includes source and model revision");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
