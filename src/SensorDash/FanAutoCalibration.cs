// A physical PWM output is identified and calibrated independently of
// temperature curves. Stage 1 verifies high-duty tach response (70-100%).
// A full 10-100% sweep is only available after a verified fan/tach pairing,
// with fresh independent temperature guards. These observations NEVER alter
// an approved temperature-to-duty curve.
internal sealed class FanAutoCalibration
{
    private static readonly int[] IdentificationDuties = [100,85,70];
    private static readonly int[] ResponseDuties = [100,85,70,55,40,30,20,15,10];
    internal const int MaximumSeconds = 240;
    private readonly List<FanCalibrationPoint> _measurements = [];
    private readonly List<(DateTimeOffset Time,double Rpm)> _recentRpm = [];
    private readonly Dictionary<string,double> _thermalBaseline = new(StringComparer.Ordinal);
    private readonly string[] _thermalGuards;
    private readonly double _temperatureLimit;
    private readonly int[] _duties;
    private DateTimeOffset _stepStarted;
    private DateTimeOffset _lastRpmObserved;
    private int _index;

    internal string ChannelId { get; }
    internal string RpmSensorId { get; }
    internal string Mode { get; }
    internal string State { get; private set; } = "running";
    internal string Message { get; private set; } = "Applying first calibration duty.";
    internal DateTimeOffset StartedAt { get; }
    internal DateTimeOffset ExpiresAt => StartedAt.AddSeconds(MaximumSeconds);
    internal bool NeedsApply { get; private set; } = true;
    internal bool Running => State == "running";
    internal int StepCount => _duties.Length;
    internal int CompletedSteps => _index;
    internal int CurrentDuty => _duties[Math.Min(_index, _duties.Length-1)];
    internal IReadOnlyList<FanCalibrationPoint> Measurements => _measurements;
    internal object Snapshot() => new
    {
        channelId=ChannelId,rpmSensorId=RpmSensorId,mode=Mode,
        state=State,message=Message,startedAt=StartedAt,
        expiresAt=ExpiresAt,currentDuty=CurrentDuty,
        completedSteps=CompletedSteps,totalSteps=StepCount,
        measurements=_measurements.ToArray()
    };

    internal FanAutoCalibration(string channelId,string rpmSensorId,
        double minimumDuty,double maximumDuty,DateTimeOffset now,
        string mode="identify",IEnumerable<string>? temperatureSensors=null,
        double temperatureLimit=85)
    {
        if(mode is not ("identify" or "response"))
            throw new ArgumentException("Unknown calibration phase.");
        ChannelId=channelId;
        RpmSensorId=rpmSensorId;
        Mode=mode;
        _thermalGuards=(temperatureSensors??[]).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        _temperatureLimit=temperatureLimit;
        if(mode=="response" && (_thermalGuards.Length<1 ||
            !double.IsFinite(_temperatureLimit) ||
            _temperatureLimit is <35 or >100))
            throw new ArgumentException("A full response sweep needs at least one temperature guard and a valid temperature limit.");
        _duties=(mode=="identify"?IdentificationDuties:ResponseDuties)
            .Where(d=>d>=Math.Max(mode=="identify"?70:10,minimumDuty)
                && d<=maximumDuty).ToArray();
        if(_duties.Length<2)
            throw new ArgumentException("Not enough supported PWM steps for calibration.");
        StartedAt=now;
        _stepStarted=now;
    }

    internal void MarkApplied(DateTimeOffset now)
    {
        NeedsApply=false;
        _stepStarted=now;
        _lastRpmObserved=default;
        _recentRpm.Clear();
        Message="Settling at "+CurrentDuty+"% PWM.";
    }

    private static double? Fresh(SensorFrame frame,string id,string kind,
        DateTimeOffset now)
    {
        var sensor=frame.Sensors.FirstOrDefault(s=>s.Id==id && s.Type==kind);
        if(sensor?.ObservedAt is not { } observed ||
            Math.Abs((now-observed).TotalSeconds)>4 ||
            sensor.Value is not { } value || !double.IsFinite(value))
            return null;
        return value;
    }

    // Called before every PWM command and on every poll. User-selected
    // thermal guards are independent of temperature curves, so calibration
    // can precede a curve. Full sweeps need fresh guard observations.
    internal bool GuardsSafe(SensorFrame frame,DateTimeOffset now)
    {
        if(!Running)return false;
        foreach(var id in _thermalGuards)
        {
            var temp=Fresh(frame,id,"Temperature",now);
            if(temp is not { } value || value>=_temperatureLimit)
            {
                Abort("Temperature guard is stale, absent or too hot; restoring prior control.");
                return false;
            }
            if(!_thermalBaseline.TryGetValue(id,out var baseline))
                _thermalBaseline[id]=baseline=value;
            if(value>baseline+6)
            {
                Abort("Temperature increased by more than 6°C during calibration; restoring prior control.");
                return false;
            }
        }
        return true;
    }

    // Three successive fresh tach samples must be stable after a settle
    // period, and the independent Super I/O readback must agree with the duty.
    internal bool Observe(SensorFrame frame,DateTimeOffset now)
    {
        if(!Running || NeedsApply)return false;
        if(now>=ExpiresAt)
        {
            Abort("Calibration timed out; restoring prior control.");
            return true;
        }
        if(!GuardsSafe(frame,now))return true;
        if((now-_stepStarted).TotalSeconds<6)return false;
        var duty=Fresh(frame,ChannelId,"Control",now);
        var rpm=Fresh(frame,RpmSensorId,"Fan",now);
        if(rpm is <=0)
        {
            Abort("Zero RPM observed; calibration stopped immediately.");
            return true;
        }
        if(duty is not { } observedDuty || Math.Abs(observedDuty-CurrentDuty)>4 ||
            rpm is not { } measured)
        {
            if((now-_stepStarted).TotalSeconds>=22)
            {
                Abort("PWM/RPM feedback did not match or become available; restoring control.");
                return true;
            }
            _recentRpm.Clear();
            return false;
        }
        if(now>_lastRpmObserved)
        {
            _lastRpmObserved=now;
            _recentRpm.Add((now,measured));
            if(_recentRpm.Count>4)_recentRpm.RemoveAt(0);
        }
        if(_recentRpm.Count>=3)
        {
            var values=_recentRpm.TakeLast(3).Select(p=>p.Rpm).ToArray();
            var mean=values.Average();
            if(values.Max()-values.Min()<=Math.Max(100,mean*0.08))
            {
                _measurements.Add(new FanCalibrationPoint{
                    DutyPercent=CurrentDuty,Rpm=mean,ObservedAt=now
                });
                _index++;
                _recentRpm.Clear();
                if(_index==_duties.Length)
                {
                    State="complete";
                    Message=Mode=="identify"
                        ?"High-duty pairing measured. Confirm the physical fans and save the mapping."
                        :"PWM/RPM response measured. Save it before assigning temperature curves.";
                }
                else
                {
                    NeedsApply=true;
                    Message="Moving to "+CurrentDuty+"% PWM.";
                }
                return true;
            }
        }
        if((now-_stepStarted).TotalSeconds>=22)
        {
            Abort("RPM failed to settle within 22 seconds; restoring control.");
            return true;
        }
        return false;
    }

    internal void Abort(string reason)
    {
        if(!Running)return;
        State="aborted";
        Message=reason;
        NeedsApply=false;
    }
}
