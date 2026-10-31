using System.Globalization;
using System.Text;
using System.Text.Json;

// Gauge-like metadata snapshots are deliberately observational. The collector
// continues to own cooling topology, aliases, and all physical fan control.
// Status 0 tombstones old tag sets after edits so instant queries do not show
// deleted bindings or aliases from Prometheus's normal 5-minute lookback.
internal static class TelemetryMetadata
{
    private const int MaxMetadataLines = 3500;

    internal static HashSet<string> Build(SensorFrame frame, DashboardSettings settings, string host)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(frame.Error) || frame.Sensors.Count == 0)
            return result;
        var h = TelemetryLineProtocol.Label(host, 80);
        var sensors = frame.Sensors
            .Where(s => !string.IsNullOrWhiteSpace(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var sensor in sensors.Values.Take(2500))
        {
            var id = TelemetryLineProtocol.Label(sensor.Id, 256);
            var kind = TelemetryLineProtocol.Label(sensor.Type, 40);
            var name = TelemetryLineProtocol.Label(sensor.Name, 96);
            var device = TelemetryLineProtocol.Label(sensor.Device, 96);
            var alias = settings.Aliases?.GetValueOrDefault(sensor.Id);
            result.Add("sensordash_sensor_info,host=" + h +
                ",sensor=" + id + ",kind=" + kind + ",name=" + name +
                ",alias=" + TelemetryLineProtocol.Label(
                    string.IsNullOrWhiteSpace(alias) ? "unassigned" : alias, 96) +
                ",device=" + device);
        }
        foreach (var system in (settings.CoolingSystems ?? []).Take(20))
        foreach (var component in (system.Components ?? []).Take(32))
        foreach (var sensorId in (component.SensorIds ?? []).Distinct(StringComparer.Ordinal).Take(16))
        {
            if (!sensors.TryGetValue(sensorId, out var sensor)) continue;
            result.Add("sensordash_cooling_binding,host=" + h +
                ",sensor=" + TelemetryLineProtocol.Label(sensorId, 256) +
                ",system=" + TelemetryLineProtocol.Label(system.Id, 80) +
                ",system_name=" + TelemetryLineProtocol.Label(system.Name, 96) +
                ",component=" + TelemetryLineProtocol.Label(component.Id, 80) +
                ",component_name=" + TelemetryLineProtocol.Label(component.Name, 96) +
                ",role=" + TelemetryLineProtocol.Label(component.Kind, 40) +
                ",kind=" + TelemetryLineProtocol.Label(sensor.Type, 40));
        }
        // Defense in depth against unexpectedly large sensor inventories.
        return result.Count <= MaxMetadataLines
            ? result
            : throw new InvalidDataException("Telemetry metadata exceeds the expected inventory size.");
    }

    internal static string Serialize(HashSet<string> current, HashSet<string> previous, long timeMs)
    {
        var lines = new StringBuilder();
        foreach (var prefix in current.OrderBy(x => x, StringComparer.Ordinal))
            lines.Append(prefix).Append(" value=1 ").Append(timeMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var prefix in previous.Except(current).OrderBy(x => x, StringComparer.Ordinal))
            lines.Append(prefix).Append(" value=0 ").Append(timeMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return lines.ToString();
    }

    internal static List<string> Chunks(string payload, int maximumBytes = 128 * 1024)
    {
        // Each line is ASCII by construction. Keep every POST well below the
        // Python gateway's 256 KiB request limit, with no partial line.
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + 1 > maximumBytes)
                throw new InvalidDataException("Metadata line is too large.");
            if (current.Length + line.Length + 1 > maximumBytes)
            {
                batches.Add(current.ToString());
                current.Clear();
            }
            current.Append(line).Append('\n');
        }
        if (current.Length > 0) batches.Add(current.ToString());
        return batches;
    }

    internal static HashSet<string> Load(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        var values = JsonSerializer.Deserialize<List<string>>(
            File.ReadAllText(path), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Malformed telemetry metadata snapshot.");
        if (values.Count > MaxMetadataLines ||
            values.Any(v => string.IsNullOrWhiteSpace(v) ||
                (!v.StartsWith("sensordash_sensor_info,", StringComparison.Ordinal) &&
                 !v.StartsWith("sensordash_cooling_binding,", StringComparison.Ordinal)) ||
                v.Contains('\n') || v.Contains('\r')))
            throw new InvalidDataException("Invalid stored metadata lines.");
        return values.ToHashSet(StringComparer.Ordinal);
    }

    internal static void Save(string path, HashSet<string> snapshot)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(
            snapshot.OrderBy(x => x, StringComparer.Ordinal).ToList(), JsonSerializerOptions.Web));
        File.Move(tmp, path, true);
    }
}

internal static class TelemetryMetadataSelfTest
{
    internal static void Run()
    {
        static void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Metadata self-test: " + reason);
        }
        var frame = new SensorFrame(DateTimeOffset.UtcNow,
        [
            new SensorReading("/cpu/0/temperature/0", "CPU Package", "AMD CPU", "Cpu",
                "Temperature", 51.2),
            new SensorReading("/cpu/0/power/0", "CPU Power", "AMD CPU", "Cpu",
                "Power", 80),
            new SensorReading("/gpu/0/temperature/0", "GPU Core", "Radeon GPU", "Gpu",
                "Temperature", 35)
        ], null);
        var config = new DashboardSettings
        {
            Aliases = new() { ["/cpu/0/temperature/0"] = "CPU Coolant Proxy" },
            CoolingSystems =
            [
                new CoolingSystemConfig
                {
                    Id = "loop-one", Name = "Walnut Loop", Kind = "liquid",
                    Components =
                    [
                        new CoolingComponentConfig
                        {
                            Id = "heat-cpu", Name = "CPU Heat Source", Kind = "heat-source",
                            SensorIds = ["/cpu/0/temperature/0", "/cpu/0/power/0"]
                        }
                    ]
                }
            ]
        };
        var previous = TelemetryMetadata.Build(frame, config, "My Desktop");
        Check(previous.Count == 5, "three sensor descriptors plus two cooling bindings");
        Check(previous.Any(x => x.Contains("alias=CPU_Coolant_Proxy") && x.Contains("sensor=/cpu/0/temperature/0")),
            "alias is informational and stripped of spaces");
        Check(previous.Any(x => x.StartsWith("sensordash_cooling_binding,") &&
            x.Contains("system=loop-one") && x.Contains("component=heat-cpu")),
            "stable cooling component keys");
        var snapshot = TelemetryMetadata.Serialize(previous, new(StringComparer.Ordinal), 1793450400000);
        Check(snapshot.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5,
            "snapshot count");
        config.Aliases["/cpu/0/temperature/0"] = "CPU Temperature";
        config.CoolingSystems[0].Components[0].SensorIds.Remove("/cpu/0/power/0");
        var current = TelemetryMetadata.Build(frame, config, "My Desktop");
        var changes = TelemetryMetadata.Serialize(current, previous, 1793450460000);
        Check(changes.Split(" value=0 ", StringSplitOptions.None).Length - 1 == 2,
            "tombstone changed alias and removed binding");
        Check(changes.Contains("alias=CPU_Temperature value=1 "), "new alias published");
        var chunks = TelemetryMetadata.Chunks(string.Concat(Enumerable.Repeat(snapshot, 500)));
        Check(chunks.Count > 1 && chunks.All(x => Encoding.ASCII.GetByteCount(x) <= 128 * 1024),
            "metadata requests chunk safely");
        var dir = Path.Combine(Path.GetTempPath(), "sensordash-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "metadata.json");
            TelemetryMetadata.Save(path, current);
            Check(TelemetryMetadata.Load(path).SetEquals(current), "persisted snapshot");
        }
        finally { Directory.Delete(dir, true); }
        Check(TelemetryMetadata.Build(new SensorFrame(DateTimeOffset.UtcNow, [], "Offline"), config, "PC").Count == 0,
            "offline frame must not trigger tombstones");
    }
}
