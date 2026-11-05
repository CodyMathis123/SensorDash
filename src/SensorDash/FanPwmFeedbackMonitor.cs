// A 60-second supervisory check: first prefer the independent Super I/O
// Control register; if a PWM/RPM curve has been verified, compare tachometer
// RPM at a *stable* command to that measured baseline as a secondary signal.
// The monitor only REQUESTS a one-shot rearm of the approved output; no direct
// hardware writes, new authorizations, or lower duty commands are possible.
internal sealed class FanPwmFeedbackMonitor
{
    internal sealed record Reading(string State,DateTimeOffset? CheckedAt,
        double? CommandPercent,double? RegisterPercent,
        DateTimeOffset? LastCorrection,string Message,
        double? ObservedRpm=null,double? EstimatedRpm=null);
    private sealed class Channel
    {
        internal DateTimeOffset NextCheck;
        internal DateTimeOffset? LastCorrection;
        internal double? PreviousDuty;
        internal bool HadMismatch;
        internal Reading Latest=new("waiting",null,null,null,null,
            "Waiting for a fresh PWM feedback observation.");
    }
    private readonly Dictionary<string,Channel> _channels=new(StringComparer.Ordinal);
    internal const int CheckSeconds=60;
    internal const int CooldownSeconds=300;
    internal const double TolerancePercent=4.0;
    internal IReadOnlyDictionary<string,Reading> Snapshot()=>
        _channels.ToDictionary(p=>p.Key,p=>p.Value.Latest,StringComparer.Ordinal);
    internal void Clear()=>_channels.Clear();

    private static double? EstimateRpm(FanChannelMapping? mapping,double duty)
    {
        if(mapping?.Kind!="fan" || !mapping.VerifiedAt.HasValue ||
            !FanCommissioningRules.HasVerifiedResponse(mapping))
            return null;
        var points=mapping.Measurements.OrderBy(p=>p.DutyPercent).ToArray();
        if(points.Length<2 || duty<points[0].DutyPercent ||
            duty>points[^1].DutyPercent)return null;
        var index=Array.FindIndex(points,p=>p.DutyPercent>=duty);
        if(index<0)return null;
        if(index==0)return points[0].Rpm;
        var lo=points[index-1];var hi=points[index];
        return hi.DutyPercent==lo.DutyPercent?lo.Rpm:
            lo.Rpm+(hi.Rpm-lo.Rpm)*(duty-lo.DutyPercent)/
                (hi.DutyPercent-lo.DutyPercent);
    }

    private static double? FreshRpm(FanChannelMapping? mapping,
        SensorFrame frame,DateTimeOffset now)
    {
        if(string.IsNullOrWhiteSpace(mapping?.RpmSensorId))return null;
        var reading=frame.Sensors.FirstOrDefault(s=>
            s.Id==mapping.RpmSensorId && s.Type=="Fan" && s.ObservedAt.HasValue &&
            Math.Abs((now-s.ObservedAt.Value).TotalSeconds)<=5 &&
            s.Value is double rpm && double.IsFinite(rpm) && rpm>=0);
        return reading?.Value;
    }

    internal (bool Rearm,bool Recovered,string Message) Check(
        string id,double? lastCommand,SensorFrame frame,DateTimeOffset now,
        FanChannelMapping? mapping=null)
    {
        if(!_channels.TryGetValue(id,out var state))
        {
            state=new Channel();
            _channels[id]=state;
        }
        if(now<state.NextCheck)return(false,false,"");
        state.NextCheck=now.AddSeconds(CheckSeconds);
        // The Super I/O register was sampled before this tick's PWM writes.
        var reading=frame.Sensors.FirstOrDefault(s=>
            s.Id==id && s.Type=="Control" && s.ObservedAt.HasValue &&
            Math.Abs((now-s.ObservedAt.Value).TotalSeconds)<=5 &&
            s.Value is double v && double.IsFinite(v));
        var register=reading?.Value;
        if(lastCommand is not { } expected || register is not { } actual)
        {
            state.PreviousDuty=lastCommand;
            state.Latest=new("unavailable",now,lastCommand,register,
                state.LastCorrection,"Fresh PWM register feedback unavailable.");
            return(false,false,"");
        }
        var stableDuty=state.PreviousDuty.HasValue &&
            Math.Abs(state.PreviousDuty.Value-expected)<1.5;
        state.PreviousDuty=expected;
        var estimatedRpm=EstimateRpm(mapping,expected);
        var observedRpm=FreshRpm(mapping,frame,now);
        var rpmMismatch=stableDuty && estimatedRpm is { } predicted &&
            observedRpm is { } measured &&
            Math.Abs(measured-predicted)>Math.Max(250,predicted*0.40);
        var registerMismatch=Math.Abs(expected-actual)>=TolerancePercent;
        if(!registerMismatch&&!rpmMismatch)
        {
            var recovered=state.HadMismatch;
            state.HadMismatch=false;
            state.Latest=new("matched",now,expected,actual,state.LastCorrection,
                "PWM register and available calibrated RPM agree with approved command.",
                observedRpm,estimatedRpm);
            return(false,recovered,recovered?
                $"PWM feedback recovered: command={expected:0.#}% readback={actual:0.#}% "+
                $"rpm={observedRpm:0} expectedRpm={estimatedRpm:0}":"");
        }
        state.HadMismatch=true;
        var discrepancy=registerMismatch
            ?$"PWM register mismatch: command={expected:0.#}% readback={actual:0.#}%"
            :$"Calibrated RPM mismatch at {expected:0.#}% PWM: observed={observedRpm:0} expected={estimatedRpm:0}";
        var cooldown=state.LastCorrection is { } previous &&
            now-previous<TimeSpan.FromSeconds(CooldownSeconds);
        state.Latest=new(cooldown?"mismatch-cooldown":"mismatch-correcting",
            now,expected,actual,state.LastCorrection,
            cooldown?"Mismatch persists; recovery cooldown active.":
                "Reasserting the same approved PWM duty once.",
            observedRpm,estimatedRpm);
        if(cooldown)return(false,false,discrepancy+"; recovery cooldown active.");
        state.LastCorrection=now;
        state.Latest=state.Latest with{LastCorrection=now};
        return(true,false,discrepancy+
            "; resetting and reapplying the already-approved PWM output.");
    }
}
