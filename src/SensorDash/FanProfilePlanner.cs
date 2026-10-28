// Pure temperature-to-duty planning; holds hysteresis state, never opens hardware.
// Missing, stale and invalid sources immediately demand maximum duty, with no
// last-known-good grace period for a live controller.
internal sealed record FanDutyDecision(double Percent, bool Failsafe, string Reason);

internal sealed class FanProfilePlanner
{
    private readonly Dictionary<string, double> _held = new(StringComparer.Ordinal);

    internal FanDutyDecision Evaluate(ApprovedFanProfile profile, SensorFrame frame, DateTimeOffset now)
    {
        var samples = WorkspaceLogic.Evaluate(frame,
            new DashboardSettings { SimulatedFanCurves = profile.Curves }, now);
        if (samples.Count != profile.Curves.Count || samples.Count == 0 ||
            samples.Any(s => s.State != "simulated" || s.InputTemperature is not double t ||
                             !double.IsFinite(t) || s.TargetPercent is not double duty ||
                             !double.IsFinite(duty)))
            return new FanDutyDecision(100, true, "Temperature source missing, invalid or stale.");

        var targets = new List<double>(samples.Count);
        for (var i = 0; i < samples.Count; i++)
        {
            var curve = profile.Curves[i];
            var current = samples[i].InputTemperature!.Value;
            var key = profile.Id + "|" + curve.Id;
            if (_held.TryGetValue(key, out var previous) &&
                current < previous && current > previous - profile.HysteresisDegrees)
                current = previous;
            else
                _held[key] = current;

            targets.Add(FanCurveMath.Interpolate(curve.Points, current));
        }
        return new FanDutyDecision(
            Math.Clamp(Math.Max(profile.MinimumPercent, targets.Max()), 0, 100),
            false, "Max of " + targets.Count + " active temperature curve(s).");
    }

    internal void Reset(string profileId)
    {
        foreach (var key in _held.Keys.Where(k => k.StartsWith(profileId + "|", StringComparison.Ordinal)).ToArray())
            _held.Remove(key);
    }

    internal void Clear() => _held.Clear();
}
