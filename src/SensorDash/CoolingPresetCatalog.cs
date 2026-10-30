using System.Text.Json;

// Catalog files ship inside the executable. Loading never accesses the network
// or modifies saved component snapshots / approved fan controls.
internal static class CoolingPresetCatalog
{
    internal static readonly JsonElement[] Items = Load();

    private static JsonElement[] Load()
    {
        var assembly = typeof(CoolingPresetCatalog).Assembly;
        const string prefix = "SensorDash.CoolingPresets.";
        var items = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            var item = document.RootElement;
            var id = item.GetProperty("id").GetString()!;
            if (item.GetProperty("schemaVersion").GetInt32() != 1 ||
                item.GetProperty("revision").GetInt32() < 1 ||
                item.GetProperty("kind").GetString() is not ("fan-group" or "pump") ||
                name != prefix + id + ".json" || !ids.Add(id))
                throw new InvalidDataException("Invalid embedded cooling preset: " + name);
            items.Add(item.Clone());
        }
        if (items.Count == 0) throw new InvalidDataException("Cooling presets were not packaged.");
        return items.ToArray();
    }

    internal static void SelfTest()
    {
        foreach (var item in Items)
        {
            var electrical = JsonSerializer.Deserialize<CoolingElectricalConfig>(item.GetProperty("electrical"), JsonSerializerOptions.Web)!;
            if (item.GetProperty("sources").GetArrayLength() == 0 ||
                (AccessoryPowerModel.Rating(electrical) is null && electrical.PowerCurve.Count < 2))
                throw new InvalidDataException("Embedded preset has no source or usable specifications.");
            // Prove the endpoint's JSON remains usable after each document is disposed.
            _ = JsonSerializer.Serialize(item);
        }
    }
}
