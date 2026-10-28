using LibreHardwareMonitor.Hardware;

// The hardware boundary is deliberately tiny, so the real LHM adapter and fake
// PWM outputs exercise exactly the same writer logic on the collection thread.
internal interface IFanPwmChannel
{
    double Minimum { get; }
    double Maximum { get; }
    bool IsSoftwareMode { get; }
    double SoftwareValue { get; }
    void SetSoftware(double percent);
    void SetDefault();
}

internal sealed class LhmPwmChannel(IControl control) : IFanPwmChannel
{
    public double Minimum => control.MinSoftwareValue;
    public double Maximum => control.MaxSoftwareValue;
    public bool IsSoftwareMode => control.ControlMode == ControlMode.Software;
    public double SoftwareValue => control.SoftwareValue;
    public void SetSoftware(double percent) => control.SetSoftware((float)percent);
    public void SetDefault() => control.SetDefault();
}

internal static class FanPwmChannelAdapter
{
    internal static Dictionary<string, IFanPwmChannel> Wrap(IReadOnlyDictionary<string, IControl> controls) =>
        controls.ToDictionary(p => p.Key, p => (IFanPwmChannel)new LhmPwmChannel(p.Value),
            StringComparer.Ordinal);
}

// Single-threaded hardware writer. Never trusts a cached Software mode on first
// takeover: LHM can remember one from a prior instance. Rising demand and
// failsafe duty are immediate; ordinary decreases are smoothed using elapsed time.
internal sealed class FanPwmWriter
{
    private sealed record LastCommand(double Duty, DateTimeOffset Time);
    private readonly Dictionary<string, LastCommand> _last = new(StringComparer.Ordinal);
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);

    internal string? Apply(string id, IFanPwmChannel channel, FanDutyDecision demand,
        double responseSeconds, DateTimeOffset now)
    {
        try
        {
            var minimum = Math.Max(0, channel.Minimum);
            var maximum = Math.Min(100, channel.Maximum);
            if (!double.IsFinite(minimum) || !double.IsFinite(maximum) ||
                minimum > maximum || maximum <= 0)
            {
                // A previously writable channel can become invalid after a
                // device reconnect. Never keep commanding its old duty.
                // Preserve ownership if restore fails so the next tick retries.
                if (_written.Contains(id))
                {
                    try { channel.SetDefault(); _written.Remove(id); _last.Remove(id); }
                    catch { return id + " has unsupported PWM range; firmware restore failed"; }
                }
                return id + " has unsupported PWM range";
            }

            var duty = Math.Clamp(demand.Percent, minimum, maximum);
            if (_last.TryGetValue(id, out var previous) && duty < previous.Duty && !demand.Failsafe)
            {
                var elapsed = Math.Clamp((now - previous.Time).TotalSeconds, 0, 10);
                var fraction = responseSeconds <= 0 ? 1 :
                    1 - Math.Exp(-elapsed / Math.Max(1, responseSeconds));
                duty = previous.Duty + (duty - previous.Duty) * fraction;
            }
            var first = !_written.Contains(id);
            if (first && channel.IsSoftwareMode) channel.SetDefault();
            if (first || !channel.IsSoftwareMode ||
                !double.IsFinite(channel.SoftwareValue) ||
                Math.Abs(channel.SoftwareValue - duty) > 0.45)
                channel.SetSoftware(duty);
            _written.Add(id);
            _last[id] = new LastCommand(duty, now);
            return null;
        }
        catch (Exception ex)
        {
            try { channel.SetDefault(); } catch { /* restoration may be unavailable */ }
            _written.Remove(id);
            _last.Remove(id);
            return id + " write failed (" + ex.GetType().Name + "); firmware recovery unconfirmed";
        }
    }

    internal bool Release(string id, IReadOnlyDictionary<string, IFanPwmChannel> channels)
    {
        if (!channels.TryGetValue(id, out var channel)) return false;
        try
        {
            channel.SetDefault();
            _written.Remove(id);
            _last.Remove(id);
            return true;
        }
        catch { return false; }
    }

    internal void ResetProfile(IEnumerable<string> channelIds)
    {
        // Only reset the ramp state; retaining ownership avoids an unnecessary
        // firmware-mode transition during a deliberate, authorized profile edit.
        foreach (var id in channelIds) _last.Remove(id);
    }

    internal void ReleaseAll(IReadOnlyDictionary<string, IFanPwmChannel> channels)
    {
        foreach (var id in _written.ToArray()) Release(id, channels);
        _written.Clear();
        _last.Clear();
    }
}
