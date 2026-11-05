// A physical PWM output is identified and calibrated independently of
// temperature curves. Stage 1 verifies high-duty tach response (70-100%).
// A full 10-100% sweep is only available after a verified fan/tach pairing,
// with fresh independent temperature guards. These observations NEVER alter
// an approved temperature-to-duty curve.
internal sealed class FanAutoCalibration
{
    private static readonly int[] IdentificationDuties = [100,85,70];
    private static readonly int[] ResponseDuties = [100,85,70,55,40,30,20,15,10];
    // Nine speeds can legitimately need 30-60 seconds each to settle.
    // Never accept an unstable RPM solely to satisfy a short timeout.
    internal const int MaximumSeconds = 600;
    internal const int MaximumStepSeconds = 60;
    internal const int MinimumSettleSeconds = 8;
    internal const int MinimumStageSeconds = 20;
    internal const int StableWindowSeconds = 12;
    internal const int MinimumStableSamples = 8;
    private readonly List<FanCalibrationPoint> _measurements = [];
    private readonly List<(DateTimeOffset Time,double Rpm)> _recentRpm = [];
    private readonly Dictionary<string,double> _thermalBaseline = new(StringComparer.Ordinal);
    private readonly string[] _thermalGuards;
    private readonly double _temperatureLimit;
    private readonly int[] _duties;
    private DateTimeOffset _stepStarted;
    private DateTimeOffset _lastRpmObserved;
    private double? _observedRpm;
    private string _stability = "waiting";
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
    internal double? ObservedRpm => _observedRpm;
    internal string Stability => _stability;
    internal int StableSampleCount => _recentRpm.Count;
    internal IReadOnlyList<FanCalibrationPoint> Measurements => _measurements;
    internal object Snapshot() => new
    {
        channelId=ChannelId,rpmSensorId=RpmSensorId,mode=Mode,
        state=State,message=Message,startedAt=StartedAt,
        expiresAt=ExpiresAt,currentDuty=CurrentDuty,
        completedSteps=CompletedSteps,totalSteps=StepCount,
        observedRpm=_observedRpm,stability=_stability,
        stableSampleCount=_recentRpm.Count,
        requiredStableSamples=MinimumStableSamples,
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
        _observedRpm=null;
        _stability="settling";
        Message="Applying "+CurrentDuty+"% fan speed; waiting for the fan to settle.";
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

    // A step is accepted only after:
    // - a minimum spin-up delay (no early readings are counted),
    // - >= 8 DISTINCT tachometer observations spanning >= 10 seconds,
    // - a bounded RPM range AND no meaningful rising/falling trend.
    // A missed sample or register mismatch invalidates that stability window.
    // Each stage can wait up to a minute; never save a speculative RPM.
    internal bool Observe(SensorFrame frame,DateTimeOffset now)
    {
        if(!Running || NeedsApply)return false;
        if(now>=ExpiresAt)
        {
            Abort("Calibration timed out before all speeds were stable; the fan curve has been restored.");
            return true;
        }
        if(!GuardsSafe(frame,now))return true;
        var stepSeconds=(now-_stepStarted).TotalSeconds;
        var duty=Fresh(frame,ChannelId,"Control",now);
        var tach=frame.Sensors.FirstOrDefault(s=>s.Id==RpmSensorId && s.Type=="Fan");
        var rpm=Fresh(frame,RpmSensorId,"Fan",now);
        if(rpm is <=0)
        {
            Abort("Fan tachometer reported zero RPM; calibration stopped and previous control restored.");
            return true;
        }
        if(duty is not { } observedDuty || Math.Abs(observedDuty-CurrentDuty)>4 ||
            rpm is not { } measured || tach?.ObservedAt is not { } observedAt)
        {
            _recentRpm.Clear();
            _stability="feedback-unavailable";
            Message="Waiting for matching, fresh fan-speed and PWM readings at "+
                CurrentDuty+"%.";
        }
        else
        {
            _observedRpm=measured;
            if(stepSeconds<MinimumSettleSeconds)
            {
                _stability="settling";
                Message=$"Waiting for {CurrentDuty}% to settle: {measured:0} RPM "+
                    $"({Math.Max(0,MinimumSettleSeconds-stepSeconds):0}s initial delay remaining).";
            }
            else
            {
                // Distinct hardware observations, not repeated UI/collector polls.
                if(observedAt>_lastRpmObserved)
                {
                    _lastRpmObserved=observedAt;
                    _recentRpm.Add((observedAt,measured));
                    _recentRpm.RemoveAll(p=>
                        (observedAt-p.Time).TotalSeconds>StableWindowSeconds+2);
                }
                var window=_recentRpm.Count>=2
                    ?(_recentRpm[^1].Time-_recentRpm[0].Time).TotalSeconds:0;
                if(_recentRpm.Count>=MinimumStableSamples &&
                    window>=StableWindowSeconds-2 && stepSeconds>=MinimumStageSeconds)
                {
                    var values=_recentRpm.Select(p=>p.Rpm).ToArray();
                    var mean=values.Average();
                    var rpmRange=values.Max()-values.Min();
                    var times=_recentRpm.Select(p=>
                        (p.Time-_recentRpm[0].Time).TotalSeconds).ToArray();
                    var meanTime=times.Average();
                    var denominator=times.Sum(t=>(t-meanTime)*(t-meanTime));
                    var slope=denominator<=0?0:
                        times.Select((t,i)=>(t-meanTime)*(values[i]-mean)).Sum()/
                            denominator;
                    var halfway=values.Length/2;
                    var halfDrift=Math.Abs(values.Take(halfway).Average()-
                        values.Skip(halfway).Average());
                    var maxRange=Math.Max(45,mean*0.035);
                    var maxSlope=Math.Max(2,mean*0.0015);
                    var maxHalfDrift=Math.Max(24,mean*0.015);
                    var stable=rpmRange<=maxRange &&
                        Math.Abs(slope)<=maxSlope && halfDrift<=maxHalfDrift;
                    if(stable)
                    {
                        _stability="stable";
                        _measurements.Add(new FanCalibrationPoint{
                            DutyPercent=CurrentDuty,Rpm=mean,ObservedAt=now
                        });
                        _index++;
                        _recentRpm.Clear();
                        if(_index==_duties.Length)
                        {
                            State="complete";
                            Message=Mode=="identify"
                                ?"Fan pairing measured at stable speeds. Confirm the physical fans and save."
                                :"Full fan-speed calibration complete. Review and save the measured RPM values.";
                        }
                        else
                        {
                            NeedsApply=true;
                            Message=$"Saved settled {mean:0} RPM. Next speed: {CurrentDuty}%.";
                        }
                        return true;
                    }
                    _stability=Math.Abs(slope)>maxSlope?
                        slope>0?"rising":"falling":"unstable";
                    Message=_stability=="rising"
                        ?$"At {CurrentDuty}%: {measured:0} RPM and still increasing; waiting for stable speed."
                        :_stability=="falling"
                        ?$"At {CurrentDuty}%: {measured:0} RPM and still decreasing; waiting for stable speed."
                        :$"At {CurrentDuty}%: {measured:0} RPM fluctuating; waiting for stable readings.";
                }
                else
                {
                    _stability="collecting";
                    Message=$"At {CurrentDuty}%: {measured:0} RPM. "+
                        $"Checking stability ({_recentRpm.Count} readings over {window:0}s).";
                }
            }
        }
        if(stepSeconds>=MaximumStepSeconds)
        {
            Abort($"Fan speed did not stabilize at {CurrentDuty}% within "+
                $"{MaximumStepSeconds} seconds. No RPM estimate was saved for this speed; previous control restored.");
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
