// Per-hardware-family archival cadence, independent of LHM hardware polling
// and persistent PWM control. Windows SQLite remains the reliable Pi export spool.
internal static class HistoryRecordingPolicy
{
    internal static int EffectiveIntervalSeconds(
        SensorReading sensor, DashboardSettings settings, HashSet<string> referenced)
    {
        // Sensors used in cooling, alerts, tray, simulation, or explicitly
        // pinned retain the regular history cadence, even on a storage/NIC.
        if (referenced.Contains(sensor.Id))
            return settings.SaveEverySeconds;
        var familySeconds = sensor.DeviceType switch
        {
            "Storage" => settings.StorageHistorySeconds,
            "Network" => settings.NetworkHistorySeconds,
            _ => settings.SaveEverySeconds
        };
        // Collection and SQLite writes cannot exceed the global recording
        // frequency. A slower global interval is always the floor.
        return Math.Max(settings.SaveEverySeconds, familySeconds);
    }

    internal static bool IsDue(
        SensorReading sensor,
        DashboardSettings settings,
        HashSet<string> referenced,
        long frameTimestampMs,
        long? priorWriteTimestampMs,
        long? priorObservedTimestampMs)
    {
        // Storage LHM readings remain in the live frame between slow physical
        // polls. Never archive the same cached observation again just because
        // the SQLite save timer fired. Never relabel timestamps as new polls.
        if (sensor.DeviceType == "Storage" && sensor.ObservedAt is { } observed &&
            priorObservedTimestampMs is { } lastObserved &&
            observed.ToUnixTimeMilliseconds() <= lastObserved)
            return false;

        if (priorWriteTimestampMs is not { } lastWritten)
            return true;
        var intervalMs = (long)EffectiveIntervalSeconds(sensor, settings, referenced) * 1000;
        return frameTimestampMs > lastWritten && frameTimestampMs - lastWritten >= intervalMs;
    }
}
