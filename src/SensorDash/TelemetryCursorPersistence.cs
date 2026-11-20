using System.Text;
using System.Text.Json;

// The exporter uses these checkpoints only after the receiver acknowledges a
// batch. A damaged checkpoint must never wedge the outbound worker indefinitely
// or silently jump to the latest sample (skipping unsent archived readings).
internal static class TelemetryCursorPersistence
{
    // Distinct atomic replacements protect each copy from interrupted writes.
    // The backup may lag the primary by one acknowledged batch if interrupted:
    // replaying a previously acknowledged batch is safer than skipping data.
    internal static void Save<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
        AtomicWrite(path, json);
        AtomicWrite(path + ".bak", json);
    }

    internal static T LoadOrRecover<T>(
        string path,
        string kind,
        Func<T> initializeNew,
        Func<T> replayRetained,
        Func<T, bool> isValid,
        Action<string> warn)
        where T : notnull
    {
        var backupPath = path + ".bak";
        var primaryExists = File.Exists(path);
        var backupExists = File.Exists(backupPath);
        if (!primaryExists && !backupExists)
        {
            // Preserve the existing first-install policy: do not backfill the
            // local archive simply because export was enabled for the first time.
            var initial = initializeNew();
            Save(path, initial);
            return initial;
        }

        if (TryRead(path, isValid, out T primary))
        {
            // Existing installations had only one cursor file. Seed a second,
            // validated copy without changing the acknowledged export position.
            if (!TryRead(backupPath, isValid, out T _))
            {
                if (backupExists) Quarantine(backupPath);
                Save(path, primary);
                if (backupExists)
                    warn(kind + " export cursor backup was invalid; rebuilt from valid primary.");
            }
            return primary;
        }

        if (TryRead(backupPath, isValid, out T recovered))
        {
            if (primaryExists) Quarantine(path);
            Save(path, recovered);
            warn(kind + " export cursor recovered from last-known-good backup; " +
                "previously acknowledged samples may be replayed.");
            return recovered;
        }

        // No trustworthy checkpoint remains. Copy both damaged originals for
        // diagnosis and resume at the beginning of the RETAINED SQLite archive,
        // not its newest sample. The archive's retention policy still limits
        // how far back recovery can reach.
        if (primaryExists) Quarantine(path);
        if (backupExists) Quarantine(backupPath);
        var replay = replayRetained();
        Save(path, replay);
        warn(kind + " export cursor and backup unavailable/corrupt; " +
            "replaying all locally retained history. Older pruned samples cannot be restored.");
        return replay;
    }

    private static bool TryRead<T>(string path, Func<T, bool> isValid, out T result)
        where T : notnull
    {
        result = default!;
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<T>(
                File.ReadAllText(path), JsonSerializerOptions.Web);
            if (loaded is null || !isValid(loaded)) return false;
            result = loaded;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static void AtomicWrite(string path, string content)
    {
        var tmp = path + ".tmp";
        var bytes = Encoding.UTF8.GetBytes(content);
        // Flush the actual file contents before moving into place. Both files
        // are in the same directory; File.Move is an atomic NTFS name swap.
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void Quarantine(string path)
    {
        var name = path + ".corrupt-" +
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" +
            Guid.NewGuid().ToString("N")[..8];
        // COPY, never move before the replacement has been committed. If a
        // power loss occurs between quarantine and Save, the invalid file must
        // remain in place so restart re-enters archive replay instead of
        // interpreting two missing files as a first-time installation.
        File.Copy(path, name);
    }

    internal static bool ValidSampleCursor(TelemetryCursor? c) =>
        c is not null && ValidTimestamp(c.TimestampMs) &&
        ValidKey(c.SensorId) && (c.TimestampMs == 0 || c.SensorId.Length != 0);

    internal static bool ValidAccessoryCursor(AccessoryCursor? c) =>
        c is not null && ValidTimestamp(c.TimestampMs) &&
        ValidKey(c.System) && ValidKey(c.Component) &&
        (c.TimestampMs == 0 || (c.System.Length != 0 && c.Component.Length != 0));

    internal static bool ValidEventCursor(long id) => id >= 0;

    private static bool ValidTimestamp(long milliseconds) =>
        milliseconds >= 0 &&
        milliseconds <= DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

    private static bool ValidKey(string? value) =>
        value is not null && value.Length <= 1024 && !value.Contains('\0');
}
