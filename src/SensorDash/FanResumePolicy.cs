// Sleep/resume is inferred from a collector polling gap, not from unreliable
// Windows power-event delivery to an unattended service. The detection alone
// does NOT authorize any channel: only previously approved profiles can rearm.
internal static class FanResumePolicy
{
    internal static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(20);

    // Invalidate the *hardware connection* on a sleep-sized gap, not just
    // the cached PWM duty. LibreHardwareMonitor can keep a seemingly valid
    // Software mode while its underlying Super I/O handle no longer drives
    // the physical header. Reopening Computer recreates its device handles,
    // just like a collector service restart does.
    internal static bool NeedsHardwareReopen(DateTimeOffset? previous,
        DateTimeOffset now) =>
        previous.HasValue && (now - previous.Value > GapThreshold ||
                             now - previous.Value < TimeSpan.FromSeconds(-2));

    // A native Windows resume event is authoritative even when a quick
    // suspend/wake interrupts fewer than 20 seconds of collection. A large
    // polling gap is retained as a fallback when no OS signal arrives.
    internal static bool ShouldReopenHardware(DateTimeOffset? previous,
        DateTimeOffset now, bool windowsResumeNotified) =>
        windowsResumeNotified || NeedsHardwareReopen(previous, now);

    // Timed manual commissioning never survives an interrupted collection.
    // Persistent profiles are separately rearmed using the same gap detection.
    internal static bool EndManualAfterGap(DateTimeOffset? previous,
        DateTimeOffset now, bool manualSessionActive) =>
        NeedsRearm(previous, now, manualSessionActive);

    internal static bool NeedsRearm(DateTimeOffset? previous, DateTimeOffset now,
        bool hasApprovedProfiles)
    {
        return hasApprovedProfiles && NeedsHardwareReopen(previous, now);
    }
}
