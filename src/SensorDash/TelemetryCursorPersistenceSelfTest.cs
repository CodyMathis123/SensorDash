using System.Text;
using System.Text.Json;

// Runs in the compiled Windows executable's --self-test path on CI. These
// tests use real filesystem replacements and the existing SQLite archive.
internal static class TelemetryCursorPersistenceSelfTest
{
    internal static void Run(HistoryStore history, string root)
    {
        static void Check(bool ok, string why)
        {
            if (!ok) throw new InvalidOperationException("Cursor recovery self-test: " + why);
        }
        var warnings = new List<string>();
        void Warn(string message) => warnings.Add(message);

        var samplePath = Path.Combine(root, "recovery-samples.json");
        var latest = new TelemetryCursor(1793450410000, "c");
        var earlier = new TelemetryCursor(1793450400000, "a");
        TelemetryCursorPersistence.Save(samplePath, latest);
        Check(File.Exists(samplePath + ".bak"), "acknowledged checkpoint backup created");
        Check(TelemetryCursorPersistence.ValidSampleCursor(latest) &&
              !TelemetryCursorPersistence.ValidSampleCursor(new TelemetryCursor(-1, "c")) &&
              !TelemetryCursorPersistence.ValidSampleCursor(new TelemetryCursor(1, "\0")),
              "sample checkpoint semantic validation");

        TelemetryCursor ReadSample() => TelemetryCursorPersistence.LoadOrRecover(
            samplePath, "sensor",
            () => new TelemetryCursor(history.LatestTelemetryTimestamp(), "\uffff"),
            () => new TelemetryCursor(0, ""),
            TelemetryCursorPersistence.ValidSampleCursor, Warn);

        // The observed incident: a nominally nonempty, entirely NUL file.
        File.WriteAllBytes(samplePath, new byte[55]);
        Check(ReadSample() == latest, "NUL-filled primary restored from backup");
        Check(Directory.GetFiles(root, "recovery-samples.json.corrupt-*").Length == 1,
            "invalid primary preserved for diagnosis");
        Check(JsonSerializer.Deserialize<TelemetryCursor>(File.ReadAllText(samplePath),
                  JsonSerializerOptions.Web) == latest,
            "primary rewritten to acknowledged backup position");

        // Interrupted/partial JSON must also restore an older acknowledged
        // backup and allow the archive reader to resend any missed peers.
        File.WriteAllText(samplePath + ".bak",
            JsonSerializer.Serialize(earlier, JsonSerializerOptions.Web));
        File.WriteAllText(samplePath, "{\"timestampMs\":");
        Check(ReadSample() == earlier, "truncated JSON uses prior acknowledged backup");
        Check(history.ReadTelemetryBatch(earlier).Count == 2,
            "restored earlier checkpoint replays historical peers");

        // A single good primary can repair a damaged backup at startup.
        File.WriteAllBytes(samplePath + ".bak", new byte[128]);
        Check(ReadSample() == earlier, "valid primary remains authoritative");
        Check(JsonSerializer.Deserialize<TelemetryCursor>(
              File.ReadAllText(samplePath + ".bak"), JsonSerializerOptions.Web) == earlier,
            "bad backup repaired from validated primary");

        // If both checkpoints are damaged, replay the RETAINED SQLite archive,
        // not the newest row. This is deliberately distinct from first enable.
        File.WriteAllBytes(samplePath, new byte[55]);
        File.WriteAllBytes(samplePath + ".bak", new byte[55]);
        var replay = ReadSample();
        Check(replay == new TelemetryCursor(0, ""),
            "double corruption starts at retained archive beginning");
        var replayRows = history.ReadTelemetryBatch(replay);
        Check(replayRows.Count == 3 && replayRows[0].SensorId == "a" &&
              replayRows[^1].SensorId == "c",
            "double corruption does not skip retained samples");

        // Valid JSON with an impossible cursor is corruption too.
        File.WriteAllText(samplePath, "{\"timestampMs\":-1,\"sensorId\":\"x\"}");
        File.WriteAllBytes(samplePath + ".bak", new byte[20]);
        Check(ReadSample() == new TelemetryCursor(0, ""),
            "invalid timestamp cannot silently skip unsent samples");

        // A missing primary with a good backup must not be mistaken for a new
        // installation (which would otherwise jump to the newest sample).
        TelemetryCursorPersistence.Save(samplePath, earlier);
        File.Delete(samplePath);
        Check(ReadSample() == earlier, "missing primary restored from backup");

        // Only genuinely fresh installs initialize to the current end.
        var fresh = Path.Combine(root, "fresh-samples.json");
        var initialized = TelemetryCursorPersistence.LoadOrRecover(
            fresh, "sensor",
            () => new TelemetryCursor(history.LatestTelemetryTimestamp(), "\uffff"),
            () => throw new InvalidOperationException("Unexpected historical backfill"),
            TelemetryCursorPersistence.ValidSampleCursor, Warn);
        Check(initialized.SensorId == "\uffff" &&
              history.ReadTelemetryBatch(initialized).Count == 0 &&
              File.Exists(fresh + ".bak"),
            "first enable preserves no-surprise-backfill behavior");

        var powerPath = Path.Combine(root, "recovery-accessory.json");
        var accessory = new AccessoryCursor(1793450410000, "system1", "pump");
        TelemetryCursorPersistence.Save(powerPath, accessory);
        File.WriteAllBytes(powerPath, new byte[128]);
        AccessoryCursor ReadAccessory() => TelemetryCursorPersistence.LoadOrRecover(
            powerPath, "accessory",
            () => new AccessoryCursor(history.LatestAccessoryPowerTimestamp(), "\uffff", "\uffff"),
            () => new AccessoryCursor(0, "", ""),
            TelemetryCursorPersistence.ValidAccessoryCursor, Warn);
        Check(ReadAccessory() == accessory, "accessory NUL primary recovered");
        File.WriteAllBytes(powerPath, new byte[128]);
        File.WriteAllText(powerPath + ".bak", "{");
        Check(ReadAccessory() == new AccessoryCursor(0, "", ""),
            "accessory double corruption replays retained archive");
        Check(TelemetryCursorPersistence.ValidAccessoryCursor(new AccessoryCursor(0, "", "")) &&
              !TelemetryCursorPersistence.ValidAccessoryCursor(
                  new AccessoryCursor(1793450400000, null!, "pump")),
              "accessory checkpoint semantic validation");

        var eventPath = Path.Combine(root, "recovery-event.json");
        TelemetryCursorPersistence.Save(eventPath, 42L);
        File.WriteAllBytes(eventPath, new byte[3]);
        Check(TelemetryCursorPersistence.LoadOrRecover(
            eventPath, "event", () => 0L, () => 0L,
            TelemetryCursorPersistence.ValidEventCursor, Warn) == 42L,
            "event cursor restored from last-known-good backup");
        File.WriteAllBytes(eventPath, new byte[8]);
        File.WriteAllText(eventPath + ".bak", "-1");
        Check(TelemetryCursorPersistence.LoadOrRecover(
            eventPath, "event", () => 0L, () => 0L,
            TelemetryCursorPersistence.ValidEventCursor, Warn) == 0L,
            "event corruption replays retained threshold events");

        Check(warnings.Any(x => x.Contains("last-known-good backup")) &&
              warnings.Any(x => x.Contains("replaying all locally retained history")) &&
              warnings.Any(x => x.Contains("backup was invalid")),
              "operator-facing recovery warnings describe remediation");
    }
}
