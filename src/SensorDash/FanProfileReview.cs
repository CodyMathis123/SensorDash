// Pure comparison between approved collector snapshots and editable drafts.
// Never changes PWM ownership: only the existing administrator-only "start-profile"
// action may approve a new snapshot. Presentation names are intentionally ignored.
internal sealed record FanProfileReviewResult(bool NeedsReapproval, string[] Reasons);

internal static class FanProfileReview
{
    internal static FanProfileReviewResult Compare(ApprovedFanProfile live, DashboardSettings? settings)
    {
        if(settings==null)return new(false,[]);
        var draft=settings.FanProfiles?.FirstOrDefault(p=>p.Id==live.Id);
        if(draft==null)return new(true,["The saved profile draft was deleted."]);
        var reasons=new List<string>();
        if(!live.ChannelIds.SequenceEqual(draft.ChannelIds??[],StringComparer.Ordinal))
            reasons.Add("PWM output assignment changed");
        if(live.MinimumPercent!=draft.MinimumPercent ||
           live.HysteresisDegrees!=draft.HysteresisDegrees ||
           live.ResponseSeconds!=draft.ResponseSeconds)
            reasons.Add("Minimum duty, hysteresis or response changed");

        var lookup=(settings.SimulatedFanCurves??[])
            .GroupBy(c=>c.Id,StringComparer.Ordinal)
            .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        if(!live.Curves.Select(c=>c.Id).SequenceEqual(draft.CurveIds??[],StringComparer.Ordinal))
            reasons.Add("Assigned temperature curves changed");
        foreach(var approved in live.Curves)
        {
            if(!lookup.TryGetValue(approved.Id,out var current))
            {
                reasons.Add("Temperature curve was removed: "+approved.Name);
                continue;
            }
            var points=approved.Points;
            var latest=current.Points;
            if(approved.SourceSensorId!=current.SourceSensorId ||
               approved.MaxSampleAgeSeconds!=current.MaxSampleAgeSeconds ||
               latest==null || points.Count!=latest.Count ||
               !points.Zip(latest,(a,b)=>a.Temperature==b.Temperature&&a.Percent==b.Percent).All(x=>x))
                reasons.Add("Temperature curve edited: "+approved.Name);
        }
        // Editing a guard must never modify an already approved running profile.
        var liveGuards=live.ThermalGuards ?? [];
        var draftGuards=draft.ThermalGuards ?? [];
        if(liveGuards.Count!=draftGuards.Count ||
           !liveGuards.Zip(draftGuards,(a,b)=>
             a.Id==b.Id && a.SourceSensorId==b.SourceSensorId &&
             a.TriggerCelsius==b.TriggerCelsius && a.ReleaseCelsius==b.ReleaseCelsius &&
             a.TriggerAfterSeconds==b.TriggerAfterSeconds &&
             a.MinimumHoldSeconds==b.MinimumHoldSeconds &&
             a.MaxSampleAgeSeconds==b.MaxSampleAgeSeconds &&
             a.MissingInputPolicy==b.MissingInputPolicy).All(x=>x))
            reasons.Add("Thermal guard configuration changed");
        return new(reasons.Count>0,reasons.ToArray());
    }
}
