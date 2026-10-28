using System.Text.Json;
using LibreHardwareMonitor.Hardware;

// Persistent fan-control authority is distinct from ordinary dashboard settings.
// Only the administrator-only IPC server can approve or stop this snapshot.
// The collector thread is the sole owner of IControl instances.
public sealed class ApprovedFanProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ChannelIds { get; set; } = [];
    public List<SimulatedFanCurveConfig> Curves { get; set; } = [];
    public double MinimumPercent { get; set; } = 20;
    public double ResponseSeconds { get; set; } = 10;
}

public sealed record FanProfileReadout(string Id, string Name, string[] ChannelIds,
    string State, double? TargetPercent, string Message);

public sealed class PersistentFanProfiles
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string,ApprovedFanProfile> _approved = new(StringComparer.Ordinal);
    private readonly Dictionary<string,double> _previous = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writtenChannels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingReleases = new(StringComparer.Ordinal);
    private List<FanProfileReadout> _latest = [];

    public PersistentFanProfiles(string dataDirectory)
    {
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
            _approved = _approved.Values.Where(p=>p.ChannelIds.All(used.Add))
                .ToDictionary(p=>p.Id,StringComparer.Ordinal);
        }
        catch { _approved = new(StringComparer.Ordinal); }
    }

    internal static bool Valid(ApprovedFanProfile p) =>
        p.Id.Length is >0 and <=80 && p.ChannelIds.Count is >0 and <=12 &&
        p.ChannelIds.Distinct(StringComparer.Ordinal).Count()==p.ChannelIds.Count &&
        p.Curves.Count is >0 and <=8 &&
        p.ChannelIds.All(id=>id.Length is >0 and <=256) &&
        p.Curves.All(c=>c.Points.Count>=2 && c.Points.Count<=16 &&
            c.SourceSensorId.Length>0 &&
            c.Points.All(pt=>double.IsFinite(pt.Temperature) && double.IsFinite(pt.Percent)
                && pt.Percent is >=0 and <=100)) &&
        double.IsFinite(p.MinimumPercent) && p.MinimumPercent is >=0 and <=100 &&
        double.IsFinite(p.ResponseSeconds) && p.ResponseSeconds is >=0 and <=90;

    public bool HasActiveProfiles { get { lock(_sync)return _approved.Count>0; } }

    public object Snapshot()
    {
        lock(_sync) return new {
            profiles = _approved.Values.Select(p=>new {id=p.Id,name=p.Name,
                channelIds=p.ChannelIds.ToArray(),
                curveNames=p.Curves.Select(c=>c.Name).ToArray()}).ToArray(),
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
                foreach(var id in previous.Except(keep))_previous.Remove(id);
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
            foreach(var id in draft.ChannelIds)_previous.Remove(id);
            return new {accepted=true,message="Approved profile saved and enabled. Collector will continuously evaluate its curves."};
        }
    }

    // Collection thread only. The runtime passes channel controls discovered
    // using the same Computer and thread that updates temperature sensors.
    public void Tick(Dictionary<string,IControl> controls, SensorFrame frame, DateTimeOffset now)
    {
        ApprovedFanProfile[] profiles;
        string[] releases;
        lock(_sync)
        {
            profiles=_approved.Values.ToArray();
            releases=_pendingReleases.ToArray();
            _pendingReleases.Clear();
        }
        foreach(var id in releases)
        {
            if(controls.TryGetValue(id,out var control))
                try{control.SetDefault();}catch{}
            _writtenChannels.Remove(id);
        }

        var readouts=new List<FanProfileReadout>();
        foreach(var profile in profiles)
        {
            var samples=WorkspaceLogic.Evaluate(frame,new DashboardSettings{
                SimulatedFanCurves=profile.Curves
            },now);
            var invalid=samples.Count!=profile.Curves.Count||
                samples.Any(s=>s.State!="simulated"||!s.TargetPercent.HasValue);
            // Missing temperature readings (including unavailable NVMe data)
            // mean FULL FAN SPEED while this profile remains active.
            var desired=invalid ? 100 : samples.Max(x=>x.TargetPercent!.Value);
            desired=Math.Clamp(desired,profile.MinimumPercent,100);
            var problems=new List<string>();
            foreach(var id in profile.ChannelIds)
            {
                if(!controls.TryGetValue(id,out var channel))
                {
                    problems.Add(id+" missing");
                    continue;
                }
                if(!float.IsFinite(channel.MinSoftwareValue)||
                   !float.IsFinite(channel.MaxSoftwareValue)||
                   channel.MinSoftwareValue>channel.MaxSoftwareValue ||
                   channel.MaxSoftwareValue<=0)
                {
                    problems.Add(id+" unsupported");continue;
                }
                var commanded=Math.Clamp(desired,
                    Math.Max(0,channel.MinSoftwareValue),Math.Min(100,channel.MaxSoftwareValue));
                if(_previous.TryGetValue(id,out var previous) && commanded<previous && !invalid)
                {
                    // Rising temperature accelerates immediately. Falling
                    // temperature decelerates gradually, avoiding fan hunting.
                    var alpha=profile.ResponseSeconds<=0?1:
                        1-Math.Exp(-1/Math.Max(1,profile.ResponseSeconds));
                    commanded=previous+(commanded-previous)*alpha;
                }
                try
                {
                    if(!_writtenChannels.Contains(id) && channel.ControlMode==ControlMode.Software)
                        channel.SetDefault();
                    if(!_writtenChannels.Contains(id)||channel.ControlMode!=ControlMode.Software ||
                        Math.Abs(channel.SoftwareValue-commanded)>0.45)
                        channel.SetSoftware((float)commanded);
                    _writtenChannels.Add(id);
                    _previous[id]=commanded;
                }
                catch(Exception ex)
                {
                    problems.Add(id+" write failed ("+ex.GetType().Name+")");
                    try{channel.SetDefault();}catch{}
                    _writtenChannels.Remove(id);
                    _previous.Remove(id);
                }
            }
            var state=problems.Count>0?"degraded":invalid?"failsafe":"running";
            readouts.Add(new FanProfileReadout(profile.Id,profile.Name,
                profile.ChannelIds.ToArray(),state,desired,
                problems.Count>0?string.Join("; ",problems):
                    invalid?"Temperature source missing/stale: commanding fail-safe full speed.":
                    $"Max of {samples.Count} active temperature curve(s)."));
        }
        lock(_sync)_latest=readouts;
    }

    public void ReleaseBeforeClose(Dictionary<string,IControl> controls)
    {
        foreach(var id in _writtenChannels.ToArray())
        {
            if(controls.TryGetValue(id,out var c))
                try{c.SetDefault();}catch{}
        }
        _writtenChannels.Clear();
        _previous.Clear();
        lock(_sync)_latest=[];
        // Approved persistent profiles are retained; they'll resume when the
        // service restarts. Firmware control is returned on graceful stop.
    }
}
