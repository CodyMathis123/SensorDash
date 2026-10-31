// Sleep/resume is inferred from a collector polling gap, not from unreliable
// Windows power-event delivery to an unattended service. The detection alone
// does NOT authorize any channel: only previously approved profiles can rearm.
internal static class FanResumePolicy
{
    internal static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(20);

    // Timed manual commissioning never survives an interrupted collection.
    // Persistent profiles are separately rearmed using the same gap detection.
    internal static bool EndManualAfterGap(DateTimeOffset? previous,
        DateTimeOffset now, bool manualSessionActive) =>
        NeedsRearm(previous, now, manualSessionActive);

    internal static bool NeedsRearm(DateTimeOffset? previous, DateTimeOffset now,
        bool hasApprovedProfiles)
    {
        if(!hasApprovedProfiles || !previous.HasValue)return false;
        var gap=now-previous.Value;
        return gap > GapThreshold || gap < TimeSpan.FromSeconds(-2);
    }
}
