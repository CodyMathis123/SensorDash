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
    // Admin requests enqueue changes. Only the collection thread mutates the evaluator and writer.
    private readonly HashSet<string> _pendingReleases = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingProfileResets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingOutputResets = new(StringComparer.Ordinal);
    private List<FanProfileReadout> _latest = [];

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
                channelIds=p.ChannelIds.ToArray(),
                curveNames=p.Curves.Select(c=>c.Name).ToArray(),
                needsReapproval=FanProfileReview.Compare(p,settings).NeedsReapproval,
                reviewReasons=FanProfileReview.Compare(p,settings).Reasons}).ToArray(),
            readings = _latest.ToArray(), persistent = true
        };
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
                foreach(var id in previous.Except(keep))_pendingOutputResets.Add(id);
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
        if(FanResumePolicy.NeedsRearm(_lastTick,now,profiles.Length>0))
        {
            _trace?.ResumeGap(now-_lastTick!.Value,previouslyApproved,now);
            _writer.ForceRearm(previouslyApproved);
        }
        _lastTick=now;
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
                if(!controls.TryGetValue(id,out var channel))
                {
                    problems.Add(id+" missing");
                    _trace?.Observe(profile.Id,id,"missing",demand.Percent,null,false,
                        null,null,now);
                    continue;
                }
                var error=_writer.Apply(id,channel,demand,profile.ResponseSeconds,now);
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
                    _trace.Observe(profile.Id,id,error!=null?"degraded":
                        demand.Failsafe?"failsafe":"running",demand.Percent,
                        _writer.LastDuty(id),channel.IsSoftwareMode,
                        mapped?.RpmSensorId,reading?.Value,now);
                }
            }
            readouts.Add(new FanProfileReadout(profile.Id,profile.Name,
                profile.ChannelIds.ToArray(),
                problems.Count>0?"degraded":demand.Failsafe?"failsafe":"running",
                demand.Percent,problems.Count>0?string.Join("; ",problems):demand.Reason));
        }
        lock(_sync)_latest=readouts;
    }

    public void ReleaseBeforeClose(Dictionary<string,IControl> controls)
    {
        _writer.ReleaseAll(FanPwmChannelAdapter.Wrap(controls));
        _planner.Clear();
        lock(_sync)_latest=[];
        _lastTick=null;
        // Persistent approval survives clean shutdown; firmware restore is best-effort.
        // A process crash cannot invoke a software recovery callback.
    }
}
