using System.Text.Json;
using LibreHardwareMonitor.Hardware;

public sealed class PersistentFanProfiles
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string,ApprovedFanProfile> _approved = new(StringComparer.Ordinal);
    private readonly FanProfilePlanner _planner = new();
    private readonly FanPwmWriter _writer = new();
    private readonly FanControlTrace? _trace;
    private DateTimeOffset? _lastTick;
    private DateTimeOffset? _postResumeRearmDue;
    // The first takeover immediately after resume can race the motherboard's
    // Super I/O initialization. Reissue the *same approved* duty once after
    // the hardware settles, without starting new channels or changing curves.
    internal void SchedulePostResumeRearm(DateTimeOffset reopenedAt) =>
        _postResumeRearmDue = reopenedAt.AddSeconds(20);
    // Admin requests enqueue changes. Only the collection thread mutates the evaluator and writer.
    private readonly HashSet<string> _pendingReleases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingProfileResets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingOutputResets = new(StringComparer.Ordinal);
    private List<FanProfileReadout> _latest = [];
    internal const int MaxOverrideSeconds = 300;
    private sealed record TemporaryOverride(string ChannelId, double Percent,
        DateTimeOffset Expires);
    // Deliberately in-memory only: never restored after service restart or sleep.
    private readonly Dictionary<string,TemporaryOverride> _overrides = new(StringComparer.Ordinal);

    public PersistentFanProfiles(string dataDirectory) : this(dataDirectory,null) { }

    internal PersistentFanProfiles(string dataDirectory, FanControlTrace? trace)
    {
        _trace=trace;
        _path = Path.Combine(dataDirectory,"approved-fan-profiles.json");
        try
        {
            var entries=JsonSerializer.Deserialize<List<ApprovedFanProfile>>(
                File.ReadAllText(_path),JsonSerializerOptions.Web) ?? [];
            _approved = entries.Where(Valid).Take(20)
                .DistinctBy(p=>p.Id).ToDictionary(p=>p.Id,StringComparer.Ordinal);
            // A corrupt/duplicated file must never allow two profiles to
            // command the same physical output.
            var used=new HashSet<string>(StringComparer.Ordinal);
            var safe=new Dictionary<string,ApprovedFanProfile>(StringComparer.Ordinal);
            foreach(var profile in _approved.Values)
            {
                if(profile.ChannelIds.Any(used.Contains))continue;
                safe[profile.Id]=profile;
                used.UnionWith(profile.ChannelIds);
            }
            _approved=safe;
        }
        catch { _approved = new(StringComparer.Ordinal); }
    }

    internal static bool Valid(ApprovedFanProfile p) =>
        p.Id.Length is >0 and <=80 && p.ChannelIds.Count is >0 and <=12 &&
        p.ChannelIds.Distinct(StringComparer.Ordinal).Count()==p.ChannelIds.Count &&
        p.Curves.Count is >0 and <=8 &&
        p.Curves.Select(c=>c.Id).Distinct(StringComparer.Ordinal).Count()==p.Curves.Count &&
        p.ChannelIds.All(id=>id.Length is >0 and <=256) &&
        p.Curves.All(c=>c.Points.Count>=2 && c.Points.Count<=16 &&
            c.SourceSensorId.Length>0 && c.MaxSampleAgeSeconds is >=3 and <=300 &&
            c.Points.Select(pt=>pt.Temperature).Distinct().Count()==c.Points.Count &&
            c.Points.All(pt=>double.IsFinite(pt.Temperature) && double.IsFinite(pt.Percent)
                && pt.Percent is >=0 and <=100)) &&
        double.IsFinite(p.MinimumPercent) && p.MinimumPercent is >=0 and <=100 &&
        double.IsFinite(p.ResponseSeconds) && p.ResponseSeconds is >=0 and <=90 &&
        double.IsFinite(p.HysteresisDegrees) && p.HysteresisDegrees is >=0 and <=10;

    public bool HasActiveProfiles { get { lock(_sync)return _approved.Count>0; } }
    public bool OwnsChannel(string id)
    {
        lock(_sync)return _approved.Values.Any(p=>p.ChannelIds.Contains(id));
    }

    public object Snapshot(DashboardSettings? settings = null)
    {
        lock(_sync) return new {
            profiles = _approved.Values.Select(p=>new {id=p.Id,name=p.Name,
                channelIds=p.ChannelIds.ToArray(), minimumPercent=p.MinimumPercent,
                curveNames=p.Curves.Select(c=>c.Name).ToArray(),
                needsReapproval=FanProfileReview.Compare(p,settings).NeedsReapproval,
                reviewReasons=FanProfileReview.Compare(p,settings).Reasons}).ToArray(),
            readings = _latest.ToArray(),
            overrides = _overrides.Values.Select(o=>new {
                channelId=o.ChannelId,percent=o.Percent,expires=o.Expires
            }).ToArray(),
            persistent = true
        };
    }

    public object RequestOverride(FanCommand command, DashboardSettings settings,
        IReadOnlyList<FanOutputInfo> outputs)
    {
        lock(_sync)
        {
            if(command.Action=="reset-overrides")
            {
                foreach(var id in _overrides.Keys)_pendingOutputResets.Add(id);
                _overrides.Clear();
                _trace?.Event("OVERRIDE","All temporary overrides released to approved curves.");
                return new {accepted=true,message="Automatic fan curves resume on the next hardware poll."};
            }
            if(command.Action=="reset-override")
            {
                if(string.IsNullOrEmpty(command.ChannelId) || !_overrides.Remove(command.ChannelId))
                    return new {accepted=false,message="No active override for this fan channel."};
                _pendingOutputResets.Add(command.ChannelId);
                _trace?.Event("OVERRIDE","Temporary override released: "+command.ChannelId);
                return new {accepted=true,message="Automatic fan curve resumes on the next hardware poll."};
            }
            if(command.Action!="override" || !command.ConfirmExclusiveOwnership)
                return new {accepted=false,message="Explicit approval required for a temporary fan override."};
            if(command.ChannelId.Length is <3 or >256 ||
                command.DurationSeconds is <15 or >MaxOverrideSeconds ||
                !double.IsFinite(command.Percent))
                return new {accepted=false,message="Invalid override channel, duration or duty."};
            var profile=_approved.Values.FirstOrDefault(p=>p.ChannelIds.Contains(command.ChannelId));
            if(profile==null)
                return new {accepted=false,message="Enable an approved automatic profile before using temporary fan overrides."};
            // Never change pumps or unknown hardware through convenience sliders.
            var mapping=settings.FanChannelMappings?.FirstOrDefault(m=>m.ChannelId==command.ChannelId);
            if(mapping?.Kind!="fan")
                return new {accepted=false,message="Identify and save this channel as a fan before using a slider."};
            var output=outputs.FirstOrDefault(o=>o.Id==command.ChannelId);
            if(output==null || !output.Writable)
                return new {accepted=false,message="The approved fan output is no longer writable."};
            var minimum=Math.Max(10,Math.Max(profile.MinimumPercent,output.Minimum));
            var maximum=Math.Min(100,output.Maximum);
            if(minimum>maximum || command.Percent<minimum || command.Percent>maximum)
                return new {accepted=false,message=$"Override must be within {minimum:0.#}–{maximum:0.#}% for this approved fan."};
            _overrides[command.ChannelId]=new(command.ChannelId,command.Percent,
                DateTimeOffset.UtcNow.AddSeconds(command.DurationSeconds));
            _pendingOutputResets.Add(command.ChannelId);
            _trace?.Event("OVERRIDE",$"Temporary fan override approved for {command.ChannelId} at {command.Percent:0.#}%.");
            return new {accepted=true,message="Temporary fan override approved; automatic curve will resume at expiry."};
        }
    }

    private void Save(IEnumerable<ApprovedFanProfile> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp=_path+".tmp";
        File.WriteAllText(tmp,JsonSerializer.Serialize(entries.ToArray(),
            new JsonSerializerOptions(JsonSerializerOptions.Web){WriteIndented=true}));
        File.Move(tmp,_path,true);
    }

    public object Request(FanCommand command, DashboardSettings settings,
        IReadOnlyList<FanOutputInfo> outputs, string? manuallyOwnedChannel)
    {
        lock(_sync)
        {
            if(command.Action is "stop-profile" or "stop-all")
            {
                if(command.Action=="stop-profile" && !_approved.ContainsKey(command.ProfileId))
                    return new {accepted=false,message="Fan profile is not active."};
                var previous=_approved.Values.SelectMany(p=>p.ChannelIds).ToHashSet(StringComparer.Ordinal);
                var remaining=command.Action=="stop-all"
                    ? new Dictionary<string,ApprovedFanProfile>(StringComparer.Ordinal)
                    : _approved.Where(kv=>kv.Key!=command.ProfileId)
                        .ToDictionary(kv=>kv.Key,kv=>kv.Value,StringComparer.Ordinal);
                try{Save(remaining.Values);}
                catch(Exception ex){return new {accepted=false,message="Cannot save updated profile state: "+ex.Message};}
                var keep=remaining.Values.SelectMany(p=>p.ChannelIds).ToHashSet(StringComparer.Ordinal);
                foreach(var id in previous)if(!keep.Contains(id))_pendingReleases.Add(id);
                _approved=remaining;
                foreach(var id in previous.Except(keep))
                {
                    _overrides.Remove(id);
                    _pendingOutputResets.Add(id);
                }
                foreach(var id in _approved.Keys)
                    if(!remaining.ContainsKey(id))_pendingProfileResets.Add(id);
                return new {accepted=true,message="Stopped profile; restoring firmware control on the next hardware poll."};
            }

            if(command.Action!="start-profile" || !command.ConfirmExclusiveOwnership)
                return new {accepted=false,message="Confirm exclusive fan-control ownership."};
            var configured=settings.FanProfiles.FirstOrDefault(p=>p.Id==command.ProfileId);
            if(configured==null)return new {accepted=false,message="Save the fan profile first."};
            if(configured.CurveIds.Count==0 || configured.ChannelIds.Count==0)
                return new {accepted=false,message="Assign at least one curve and one writable output."};
            var lookup=settings.SimulatedFanCurves.ToDictionary(c=>c.Id);
            if(configured.CurveIds.Any(id=>!lookup.ContainsKey(id)))
                return new {accepted=false,message="The profile references a missing temperature curve."};
            var available=outputs.ToDictionary(o=>o.Id);
            if(configured.ChannelIds.Any(id=>!available.TryGetValue(id,out var o)||
                !o.Writable || o.Maximum<=0))
                return new {accepted=false,message="A selected output is unavailable or cannot be controlled."};
            if(manuallyOwnedChannel!=null && configured.ChannelIds.Contains(manuallyOwnedChannel))
                return new {accepted=false,message="Release the manual test before enabling the profile."};
            if(_approved.Values.Where(p=>p.Id!=configured.Id)
                 .Any(p=>p.ChannelIds.Intersect(configured.ChannelIds).Any()))
                return new {accepted=false,message="A physical output is already assigned to another active profile."};

            // Deep-copy *only* curves selected in this authorization. Ordinary
            // settings changes cannot silently alter speed after UAC approval.
            var draft=new ApprovedFanProfile {
                Id=configured.Id,Name=configured.Name,
                ChannelIds=[..configured.ChannelIds],
                Curves=configured.CurveIds.Select(id=>lookup[id]).Select(c=>
                    new SimulatedFanCurveConfig {
                        Id=c.Id,Name=c.Name,SourceSensorId=c.SourceSensorId,
                        MaxSampleAgeSeconds=c.MaxSampleAgeSeconds,
                        Points=c.Points.Select(pt=>new CurvePointConfig {
                            Temperature=pt.Temperature,Percent=pt.Percent }).ToList()
                    }).ToList(),
                MinimumPercent=configured.MinimumPercent,
                HysteresisDegrees=configured.HysteresisDegrees,
                ResponseSeconds=configured.ResponseSeconds
            };
            if(!Valid(draft))
                return new {accepted=false,message="Invalid temperature curve or output configuration."};
            var next=new Dictionary<string,ApprovedFanProfile>(_approved,StringComparer.Ordinal);
            if(next.TryGetValue(draft.Id,out var old))
            {
                foreach(var id in old.ChannelIds.Except(draft.ChannelIds))_pendingReleases.Add(id);
            }
            next[draft.Id]=draft;
            try{Save(next.Values);}
            catch(Exception ex){return new {accepted=false,message="Could not activate profile: "+ex.Message};}
            _approved=next;
            // Profile reapproval revokes temporary manual targets, never persists them.
            foreach(var id in draft.ChannelIds)_overrides.Remove(id);
            // Resending a deliberate user-approved edit must update hardware.
            foreach(var id in draft.ChannelIds)_pendingOutputResets.Add(id);
            _pendingProfileResets.Add(draft.Id);
            return new {accepted=true,message="Approved profile saved and enabled. Collector will continuously evaluate its curves."};
        }
    }

    // Same path for real IControl objects and test doubles.
    public void Tick(Dictionary<string,IControl> controls, SensorFrame frame, DateTimeOffset now,
        DashboardSettings? settings=null) =>
        Tick(FanPwmChannelAdapter.Wrap(controls),frame,now,settings);

    internal void Tick(IReadOnlyDictionary<string,IFanPwmChannel> controls,
        SensorFrame frame, DateTimeOffset now,DashboardSettings? settings=null)
    {
        ApprovedFanProfile[] profiles;
        string[] releases, profileResets, outputResets;
        lock(_sync)
        {
            profiles=_approved.Values.ToArray();
            releases=_pendingReleases.ToArray();
            _pendingReleases.Clear();
            profileResets=_pendingProfileResets.ToArray();
            _pendingProfileResets.Clear();
            outputResets=_pendingOutputResets.ToArray();
            _pendingOutputResets.Clear();
        }
        // Windows sleep may reset the physical PWM header to BIOS control,
        // while the LibreHardwareMonitor object still reports the old cached
        // Software mode/value. On a long collector gap, invalidate the writer's
        // ownership and force a real SetDefault/SetSoftware transition once.
        var previouslyApproved=profiles.SelectMany(p=>p.ChannelIds)
            .Distinct(StringComparer.Ordinal).ToArray();
        var previousTick=_lastTick;
        if (profiles.Length == 0) _postResumeRearmDue = null;
        if (previousTick.HasValue && _postResumeRearmDue is { } due && now >= due
            && profiles.Length > 0)
        {
            _postResumeRearmDue = null;
            _writer.ForceRearm(previouslyApproved);
            _trace?.Event("POST-RESUME-REARM",
                "Delayed second takeover after hardware reopen; retrying approved PWM outputs: "
                + string.Join(",", previouslyApproved), now);
        }
        if(FanResumePolicy.NeedsRearm(previousTick,now,profiles.Length>0))
        {
            _trace?.ResumeGap(now-previousTick!.Value,previouslyApproved,now);
            _writer.ForceRearm(previouslyApproved);
        }
        _lastTick=now;
        Dictionary<string,TemporaryOverride> overrides;
        lock(_sync)
        {
            if(FanResumePolicy.NeedsRearm(previousTick,now,profiles.Length>0))
                _overrides.Clear();
            foreach(var id in _overrides.Where(kv=>kv.Value.Expires<=now ||
                !profiles.Any(p=>p.ChannelIds.Contains(kv.Key))).Select(kv=>kv.Key).ToArray())
            {
                _overrides.Remove(id);
                _pendingOutputResets.Add(id);
            }
            overrides=new(_overrides,StringComparer.Ordinal);
            // Previously queued resets plus any expired override must be consumed this tick.
            outputResets=[..outputResets,.._pendingOutputResets];
            _pendingOutputResets.Clear();
        }
        foreach(var id in profileResets)_planner.Reset(id);
        _writer.ResetProfile(outputResets);
        foreach(var id in releases)
            if(!_writer.Release(id,controls))
                lock(_sync)_pendingReleases.Add(id);

        var readouts=new List<FanProfileReadout>();
        foreach(var profile in profiles)
        {
            var demand=_planner.Evaluate(profile,frame,now);
            var problems=new List<string>();
            foreach(var id in profile.ChannelIds)
            {
                // Sensor failures or high cooling demand outrank a lower temporary duty.
                // An intentional 100% fan boost remains allowed during high demand.
                // Reclassifying an output as a pump or unknown immediately revokes it.
                var manual=overrides.TryGetValue(id,out var entry) ? entry : null;
                if(manual!=null && (demand.Failsafe ||
                    (demand.Percent>=80 && manual.Percent<demand.Percent) ||
                    (settings!=null && !settings.FanChannelMappings.Any(m=>
                        m.ChannelId==id && m.Kind=="fan"))))
                {
                    overrides.Remove(id);
                    lock(_sync)_overrides.Remove(id);
                    _writer.ResetProfile([id]);
                    manual=null;
                }
                var applied=manual==null?demand:
                    new FanDutyDecision(manual.Percent,false,"Temporary manual override");
                if(!controls.TryGetValue(id,out var channel))
                {
                    problems.Add(id+" missing");
                    _trace?.Observe(profile.Id,id,"missing",demand.Percent,null,false,
                        null,null,null,now);
                    continue;
                }
                var error=_writer.Apply(id,channel,applied,
                    manual==null?profile.ResponseSeconds:0,now);
                if(error!=null)problems.Add(error);
                if(_trace!=null)
                {
                    var mapped=settings?.FanChannelMappings?.FirstOrDefault(m=>
                        m.ChannelId==id && m.Kind=="fan");
                    var reading=mapped==null?null:frame.Sensors.FirstOrDefault(s=>
                        s.Id==mapped.RpmSensorId && s.Type=="Fan" &&
                        s.ObservedAt.HasValue &&
                        Math.Abs((now-s.ObservedAt.Value).TotalSeconds)<=10 &&
                        s.Value is double v && double.IsFinite(v));
                    // Distinct from IControl.SoftwareValue (command-side cache):
                    // the Control sensor reports the last Super I/O register
                    // readback from hardware.Update() on this collection tick.
                    // It is sampled before this tick's PWM write.
                    var registerDuty=frame.Sensors.FirstOrDefault(s=>
                        s.Id==id && s.Type=="Control" &&
                        s.ObservedAt.HasValue &&
                        Math.Abs((now-s.ObservedAt.Value).TotalSeconds)<=10 &&
                        s.Value is double v && double.IsFinite(v))?.Value;
                    _trace.Observe(profile.Id,id,error!=null?"degraded":
                        demand.Failsafe?"failsafe":manual!=null?"override":"running",applied.Percent,
                        _writer.LastDuty(id),channel.IsSoftwareMode,
                        registerDuty,mapped?.RpmSensorId,reading?.Value,now);
                }
            }
            readouts.Add(new FanProfileReadout(profile.Id,profile.Name,
                profile.ChannelIds.ToArray(),
                problems.Count>0?"degraded":demand.Failsafe?"failsafe":
                    profile.ChannelIds.Any(overrides.ContainsKey)?"override":"running",
                demand.Percent,problems.Count>0?string.Join("; ",problems):demand.Reason));
        }
        lock(_sync)_latest=readouts;
    }

    public void ReleaseBeforeClose(Dictionary<string,IControl> controls)
    {
        _writer.ReleaseAll(FanPwmChannelAdapter.Wrap(controls));
        _planner.Clear();
        lock(_sync)
        {
            _latest=[];
            _overrides.Clear();
        }
        _lastTick=null;
        _postResumeRearmDue=null;
        // Persistent approval survives clean shutdown; firmware restore is best-effort.
        // A process crash cannot invoke a software recovery callback.
    }
}
