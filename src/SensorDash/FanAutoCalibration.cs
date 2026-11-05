// A single explicitly authorized calibration session. It never owns a fan
// outside an already-approved persistent profile; all writes still go through
// PersistentFanProfiles and its existing fail-safe PWM writer.
internal sealed class FanAutoCalibration
{
    internal static readonly int[] CandidateDuties = [100, 85, 70, 55, 40, 30, 20, 15, 10];
    internal const int MaximumSeconds = 240;
    private readonly List<FanCalibrationPoint> _measurements = [];
    private readonly List<(DateTimeOffset Time, double Rpm)> _recentRpm = [];
    private DateTimeOffset _stepStarted;
    private DateTimeOffset _lastRpmObserved;
    private readonly int[] _duties;
    private int _index;

    internal string ChannelId { get; }
    internal string RpmSensorId { get; }
    internal string State { get; private set; } = "running";
    internal string Message { get; private set; } = "Applying first calibration duty.";
    internal DateTimeOffset StartedAt { get; }
    internal DateTimeOffset ExpiresAt => StartedAt.AddSeconds(MaximumSeconds);
    internal bool NeedsApply { get; private set; } = true;
    internal bool Running => State == "running";
    internal int StepCount => _duties.Length;
    internal int CompletedSteps => _index;
    internal int CurrentDuty => _duties[Math.Min(_index, _duties.Length - 1)];
    internal IReadOnlyList<FanCalibrationPoint> Measurements => _measurements;
    internal object Snapshot() => new
    {
        channelId = ChannelId,
        rpmSensorId = RpmSensorId,
        state = State,
        message = Message,
        startedAt = StartedAt,
        expiresAt = ExpiresAt,
        currentDuty = CurrentDuty,
        completedSteps = CompletedSteps,
        totalSteps = StepCount,
        measurements = _measurements.ToArray()
    };

    internal FanAutoCalibration(string channelId, string rpmSensorId,
        double minimumDuty, double maximumDuty, DateTimeOffset now)
    {
        ChannelId = channelId;
        RpmSensorId = rpmSensorId;
        _duties = CandidateDuties.Where(d =>
            d >= Math.Max(10, minimumDuty) && d <= maximumDuty).ToArray();
        if (_duties.Length < 2)
            throw new ArgumentException("Not enough supported PWM points for calibration.");
        StartedAt = now;
        _stepStarted = now;
    }

    internal void MarkApplied(DateTimeOffset now)
    {
        NeedsApply = false;
        _stepStarted = now;
        _lastRpmObserved = default;
        _recentRpm.Clear();
        Message = "Settling at " + CurrentDuty + "% PWM.";
    }

    private static double? Fresh(SensorFrame frame, string id, string kind,
        DateTimeOffset now)
    {
        var sensor = frame.Sensors.FirstOrDefault(s => s.Id == id && s.Type == kind);
        if (sensor?.ObservedAt is not { } observed ||
            Math.Abs((now - observed).TotalSeconds) > 4 ||
            sensor.Value is not { } value || !double.IsFinite(value))
            return null;
        return value;
    }

    // Return true when a stage changes or the session ends. Samples are
    // accepted only if the *readback* and RPM sensors are fresh, the duty has
    // settled, and three independent tach samples are reasonably stable.
    internal bool Observe(SensorFrame frame, DateTimeOffset now)
    {
        if (!Running || NeedsApply) return false;
        if (now >= ExpiresAt)
        {
            Abort("Calibration expired; approved curve restored.");
            return true;
        }
        if ((now - _stepStarted).TotalSeconds < 6) return false;
        var duty = Fresh(frame, ChannelId, "Control", now);
        var rpm = Fresh(frame, RpmSensorId, "Fan", now);
        if (rpm is <= 0)
        {
            Abort("Zero RPM detected; calibration stopped and approved curve restored.");
            return true;
        }
        if (duty is not { } readback || Math.Abs(readback - CurrentDuty) > 4 ||
            rpm is not { } measured)
        {
            if ((now - _stepStarted).TotalSeconds >= 22)
            {
                Abort("PWM register/RPM feedback unavailable or mismatched; curve restored.");
                return true;
            }
            _recentRpm.Clear();
            return false;
        }
        if (now > _lastRpmObserved)
        {
            _lastRpmObserved = now;
            _recentRpm.Add((now, measured));
            if (_recentRpm.Count > 4) _recentRpm.RemoveAt(0);
        }
        if (_recentRpm.Count >= 3)
        {
            var values = _recentRpm.TakeLast(3).Select(s => s.Rpm).ToArray();
            var avg = values.Average();
            if (values.Max() - values.Min() <= Math.Max(100, avg * 0.08))
            {
                _measurements.Add(new FanCalibrationPoint
                {
                    DutyPercent = CurrentDuty,
                    Rpm = avg,
                    ObservedAt = now
                });
                _index++;
                _recentRpm.Clear();
                if (_index == _duties.Length)
                {
                    State = "complete";
                    Message = "Calibration complete; approved fan curve restored. Save measurements to keep them.";
                }
                else
                {
                    NeedsApply = true;
                    Message = "Moving to " + CurrentDuty + "% PWM.";
                }
                return true;
            }
        }
        if ((now - _stepStarted).TotalSeconds >= 22)
        {
            Abort("RPM did not settle within 22 seconds; approved curve restored.");
            return true;
        }
        return false;
    }

    internal void Abort(string reason)
    {
        if (!Running) return;
        State = "aborted";
        Message = reason;
        NeedsApply = false;
    }
}
