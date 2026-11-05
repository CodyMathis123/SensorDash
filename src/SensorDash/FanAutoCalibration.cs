// A physical PWM output is identified and calibrated independently of
// temperature curves. Stage 1 verifies high-duty tach response (70-100%).
// A full 10-100% sweep is only available after a verified fan/tach pairing,
// with fresh independent temperature guards. These observations NEVER alter
// an approved temperature-to-duty curve.
internal sealed class FanAutoCalibration
{
    private static readonly int[] IdentificationDuties = [100,85,70];
    // Descending 5% checkpoints retain denser low-speed information while
    // allowing one common physical fan calibration for CPU/GPU MAX curves.
    private static readonly int[] ResponseDuties = Enumerable.Range(0,19)
        .Select(i=>100-5*i).ToArray();
    // Nineteen checkpoints require at least 6m20s at 20s each.
    // Most runs should take ~7-19 minutes, but provide bounded headroom.
    // Abort rather than recording an unstable RPM at either timeout.
    internal const int MaximumSeconds = 1500;
    internal const int MaximumStepSeconds = 60;
    internal const int MinimumSettleSeconds = 8;
    internal const int MinimumStageSeconds = 20;
    internal const int StableWindowSeconds = 12;
    internal const int MinimumStableSamples = 8;
    private readonly List<FanCalibrationPoint> _measurements = [];
    private readonly List<(DateTimeOffset Time,double Rpm)> _recentRpm = [];
    private readonly Dictionary<string,double> _thermalBaseline = new(StringComparer.Ordinal);
    private readonly string[] _thermalGuards;
    // Freeze the user's friendly sensor names for clear diagnostic messages
    // throughout this calibration session; sensor IDs remain authoritative.
    private readonly Dictionary<string,string> _temperatureAliases=new(StringComparer.Ordinal);
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
        double temperatureLimit=85,
        IReadOnlyDictionary<string,string>? temperatureAliases=null)
    {
        if(mode is not ("identify" or "response"))
            throw new ArgumentException("Unknown calibration phase.");
        ChannelId=channelId;
        RpmSensorId=rpmSensorId;
        Mode=mode;
        _thermalGuards=(temperatureSensors??[]).Where(x=>!string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).Take(8).ToArray();
        _temperatureLimit=temperatureLimit;
        foreach(var id in _thermalGuards)
            if(temperatureAliases!=null &&
               temperatureAliases.TryGetValue(id,out var alias) &&
               !string.IsNullOrWhiteSpace(alias))
                _temperatureAliases[id]=alias.Trim();
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

    private string GuardName(string id,string? hardwareName)
    {
        if(_temperatureAliases.TryGetValue(id,out var alias))
            return alias;
        return string.IsNullOrWhiteSpace(hardwareName)?id:hardwareName;
    }

    // Called before every PWM command and on every poll. User-selected
    // thermal guards are independent of temperature curves, so calibration
    // can precede a curve. Full sweeps need fresh guard observations.
    internal bool GuardsSafe(SensorFrame frame,DateTimeOffset now)
    {
        if(!Running)return false;
        foreach(var id in _thermalGuards)
        {
            // Give the operator a concrete reason for an aborted sweep.
            // The distinction between a hot component and missing/stale
            // telemetry is important; neither is safe to ignore.
            var sensor=frame.Sensors.FirstOrDefault(s=>s.Id==id && s.Type=="Temperature");
            var label=GuardName(id,sensor?.Name);
            if(sensor==null)
            {
                Abort($"Temperature monitor {label} ({id}) was not found in the latest sensor collection. Calibration stopped; prior fan control restored.");
                return false;
            }
            if(sensor.ObservedAt is not { } observed)
            {
                Abort($"Temperature monitor {label} ({id}) has no reading timestamp. Calibration stopped; prior fan control restored.");
                return false;
            }
            var age=(now-observed).TotalSeconds;
            // A short hiccup in LHM polling should not invalidate an entire
            // 19-point sweep, but never use readings older than ten seconds
            // to authorize a lower fan speed.
            if(age>10 || age< -4)
            {
                var last=sensor.Value is { } oldValue && double.IsFinite(oldValue)
                    ?$" Last temperature: {oldValue:0.0}°C.":"";
                Abort($"Temperature monitor {label} ({id}) has stale data (last sample {age:0.0}s ago).{last} Calibration stopped; prior fan control restored.");
                return false;
            }
            if(sensor.Value is not { } value || !double.IsFinite(value))
            {
                Abort($"Temperature monitor {label} ({id}) has no valid temperature value. Calibration stopped; prior fan control restored.");
                return false;
            }
            if(value>=_temperatureLimit)
            {
                Abort($"Temperature monitor {label} reached {value:0.0}°C (configured stop limit {_temperatureLimit:0.0}°C). Calibration stopped; prior fan control restored.");
                return false;
            }
            if(!_thermalBaseline.TryGetValue(id,out var baseline))
                _thermalBaseline[id]=baseline=value;
            if(value>baseline+6)
            {
                Abort($"Temperature monitor {label} increased from {baseline:0.0}°C to {value:0.0}°C (+{value-baseline:0.0}°C), exceeding the 6°C rise limit. Calibration stopped; prior fan control restored.");
                return false;
            }
        }
        return true;
    }

    // Capture the exact thermal observations used during an accepted RPM
    // sample, not whatever temperatures happen to be current when a user
    // opens the table later. No guard exists in the identify-only preflight.
    private List<FanCalibrationTemperature> CaptureTemperatures(
        SensorFrame frame, DateTimeOffset now)
    {
        var result=new List<FanCalibrationTemperature>();
        foreach(var id in _thermalGuards)
        {
            var sensor=frame.Sensors.FirstOrDefault(s=>s.Id==id &&
                s.Type=="Temperature");
            if(sensor?.ObservedAt is not { } observed ||
               (now-observed).TotalSeconds is < -4 or > 10 ||
               sensor.Value is not { } value || !double.IsFinite(value))
                continue;
            result.Add(new FanCalibrationTemperature{
                SensorId=id,
                SensorName=GuardName(id,sensor.Name),
                Celsius=value,
                ObservedAt=observed
            });
        }
        return result;
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
                            DutyPercent=CurrentDuty,Rpm=mean,ObservedAt=now,
                            Temperatures=CaptureTemperatures(frame,now)
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
