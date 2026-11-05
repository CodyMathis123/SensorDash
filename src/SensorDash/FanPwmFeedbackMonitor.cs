// The register-derived Control sensor is independent of the command-side
// IControl mode/value. This monitor NEVER commands hardware; the caller decides
// whether to rearm an already-approved PWM output after a persistent mismatch.
internal sealed class FanPwmFeedbackMonitor
{
    internal sealed record Reading(string State, DateTimeOffset? CheckedAt,
        double? CommandPercent, double? RegisterPercent,
        DateTimeOffset? LastCorrection, string Message);
    private sealed class Channel
    {
        internal DateTimeOffset NextCheck;
        internal DateTimeOffset? LastCorrection;
        internal bool HadMismatch;
        internal Reading Latest = new("waiting", null, null, null, null,
            "Waiting for a stable, fresh PWM feedback observation.");
    }
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    internal const int CheckSeconds = 60;
    internal const int CooldownSeconds = 300;
    internal const double TolerancePercent = 4.0;
    internal IReadOnlyDictionary<string, Reading> Snapshot() =>
        _channels.ToDictionary(p=>p.Key,p=>p.Value.Latest,StringComparer.Ordinal);

    internal void Clear() => _channels.Clear();

    internal (bool Rearm, bool Recovered, string Message) Check(
        string id, double? lastCommand, SensorFrame frame, DateTimeOffset now)
    {
        if (!_channels.TryGetValue(id, out var state))
        {
            state = new Channel();
            _channels[id] = state;
        }
        if (now < state.NextCheck)
            return (false, false, "");
        state.NextCheck = now.AddSeconds(CheckSeconds);
        // LHM reads Super I/O on hardware.Update before this tick's write.
        // Only compare with the PREVIOUS tick's written duty, never the
        // current planned duty (which could legitimately be changing).
        var reading = frame.Sensors.FirstOrDefault(s =>
            s.Id == id && s.Type == "Control" && s.ObservedAt.HasValue &&
            Math.Abs((now - s.ObservedAt.Value).TotalSeconds) <= 5 &&
            s.Value is double v && double.IsFinite(v));
        var register = reading?.Value;
        if (lastCommand is not { } expected || register is not { } actual)
        {
            state.Latest = new("unavailable", now, lastCommand, register,
                state.LastCorrection, "Fresh PWM register feedback unavailable.");
            return (false, false, "");
        }
        if (Math.Abs(expected - actual) < TolerancePercent)
        {
            var recovered = state.HadMismatch;
            state.HadMismatch = false;
            state.Latest = new("matched", now, expected, actual,
                state.LastCorrection, "Controller register matches approved PWM.");
            return (false, recovered, recovered ?
                $"PWM feedback recovered: command={expected:0.#}% readback={actual:0.#}%" : "");
        }
        state.HadMismatch = true;
        var cooldown = state.LastCorrection is { } last &&
            now - last < TimeSpan.FromSeconds(CooldownSeconds);
        state.Latest = new(cooldown ? "mismatch-cooldown" : "mismatch-correcting",
            now, expected, actual, state.LastCorrection,
            cooldown ? "Mismatch persists; recovery cooldown active." :
                "Register differs from approved command; reasserting once.");
        if (cooldown)
            return (false, false,
                $"PWM mismatch persists (cooldown): command={expected:0.#}% readback={actual:0.#}%");
        state.LastCorrection = now;
        state.Latest = state.Latest with { LastCorrection = now };
        return (true, false,
            $"PWM register mismatch: command={expected:0.#}% readback={actual:0.#}%; resetting and reapplying approved duty.");
    }
}
