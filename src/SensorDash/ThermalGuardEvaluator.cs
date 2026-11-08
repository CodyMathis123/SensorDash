// Standalone threshold/hold state machine; does not open hardware or poll sensors.
// Guards are not fan curves: they request 100% only after sustained excess heat.
// Missing sources cannot be interpreted as cold. The policy is explicit per guard.
internal sealed record ThermalGuardResult(bool Active, bool Failsafe, bool Warning, string Message);

internal sealed class ThermalGuardEvaluator
{
    private sealed class State
    {
        public DateTimeOffset? AboveSince;
        public DateTimeOffset? ActivatedAt;
        public DateTimeOffset? LastEvaluated;
    }

    private readonly Dictionary<string,State> _states=new(StringComparer.Ordinal);

    internal ThermalGuardResult Evaluate(ApprovedFanProfile profile, SensorFrame frame,
        DateTimeOffset now)
    {
        var active=new List<string>();
        var missing=new List<string>();
        var warnings=new List<string>();
        foreach(var guard in profile.ThermalGuards ?? [])
        {
            var key=profile.Id+"|"+guard.Id;
            if(!_states.TryGetValue(key,out var state))
                _states[key]=state=new State();
            // Do not carry a pending 10-second timer over a sleep/wake gap,
            // or a backward clock change. A latched emergency remains latched.
            if(state.LastEvaluated is DateTimeOffset last &&
               ((now-last).TotalSeconds>guard.MaxSampleAgeSeconds ||
                (now-last).TotalSeconds<0))
                state.AboveSince=null;
            state.LastEvaluated=now;

            var sample=frame.Sensors.FirstOrDefault(s=>
                s.Id==guard.SourceSensorId && s.Type=="Temperature");
            var seen=sample?.ObservedAt ?? frame.Timestamp;
            var age=(now-seen).TotalSeconds;
            var valid=sample?.Value is double v && double.IsFinite(v) &&
                age>=-5 && age<=guard.MaxSampleAgeSeconds;
            if(!valid)
            {
                // Never silently unlatch an already active safety condition.
                state.AboveSince=null;
                if(state.ActivatedAt.HasValue)
                    active.Add(guard.Name+" (input unavailable while active)");
                else if(guard.MissingInputPolicy=="failsafe")
                    missing.Add(guard.Name);
                else warnings.Add(guard.Name+" input missing/stale");
                continue;
            }

            var temp=sample!.Value!.Value;
            if(state.ActivatedAt.HasValue)
            {
                var held=(now-state.ActivatedAt.Value).TotalSeconds;
                if(temp<=guard.ReleaseCelsius && held>=guard.MinimumHoldSeconds)
                {
                    state.ActivatedAt=null;
                    state.AboveSince=null;
                }
                else active.Add(guard.Name+" ("+temp.ToString("0.0")+"°C)");
            }
            else if(temp>=guard.TriggerCelsius)
            {
                state.AboveSince ??= now;
                if((now-state.AboveSince.Value).TotalSeconds>=guard.TriggerAfterSeconds)
                {
                    state.ActivatedAt=now;
                    active.Add(guard.Name+" ("+temp.ToString("0.0")+"°C)");
                }
            }
            else state.AboveSince=null;
        }
        if(active.Count>0)
            return new(true,false,warnings.Count>0,
                "Thermal guard active: "+string.Join(", ",active));
        if(missing.Count>0)
            return new(false,true,true,
                "Thermal guard input unavailable: "+string.Join(", ",missing));
        if(warnings.Count>0)
            return new(false,false,true,"Thermal guard warning: "+string.Join(", ",warnings));
        return new(false,false,false,"");
    }

    internal void Reset(string profileId)
    {
        foreach(var key in _states.Keys.Where(k=>k.StartsWith(profileId+"|",StringComparison.Ordinal)).ToArray())
            _states.Remove(key);
    }
    internal void Clear()=>_states.Clear();
}
