using System.Text.Json;
using LibreHardwareMonitor.Hardware;

internal sealed record FanRecoveryEvent(string Severity, string Sensor, string Message, double? Value);

public sealed class PersistentFanProfiles
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string,ApprovedFanProfile> _approved = new(StringComparer.Ordinal);
    private readonly FanProfilePlanner _planner = new();
    private readonly FanPwmWriter _writer = new();
    private readonly FanPwmFeedbackMonitor _feedback = new();
    private readonly Queue<FanRecoveryEvent> _pendingEvents = new();
    private FanAutoCalibration? _calibration;
    private bool _calibrationFinalized=true;
    private bool _standaloneCalibrationOwned;
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
    // Keep the fast hardware poll until firmware/profile restoration actually
    // completes, even if the UI has already requested cancellation.
    public bool HasCalibrationRunning { get {lock(_sync)return _calibration?.Running==true ||
        !_calibrationFinalized;} }
    public void CancelCalibration(string reason)
    {
        lock(_sync)
        {
            if(_calibration?.Running==true)_calibration.Abort(reason);
        }
    }
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
            calibration = _calibration?.Snapshot(),
            pwmFeedback = _feedback.Snapshot(),
            persistent = true
        };
    }

    internal FanRecoveryEvent[] DrainRecoveryEvents()
    {
        lock(_sync)
        {
            var events=_pendingEvents.ToArray();
            _pendingEvents.Clear();
            return events;
        }
    }

    public object RequestCalibration(FanCommand command,DashboardSettings settings,
        IReadOnlyList<FanOutputInfo> outputs)
    {
        lock(_sync)
        {
            if(command.Action=="cancel-calibration")
            {
                if(_calibration?.Running!=true)
                    return new {accepted=false,message="No calibration is running."};
                _calibration.Abort("Cancelled; restoring approved curve or firmware control.");
                return new {accepted=true,message="Calibration cancellation queued; previous control will be restored."};
            }
            if(command.Action!="calibrate" || !command.ConfirmExclusiveOwnership)
                return new {accepted=false,message="Explicit administrator authorization required."};
            if(_calibration?.Running==true || !_calibrationFinalized)
                return new {accepted=false,message="Finish the previous calibration before starting another."};
            var map=settings.FanChannelMappings?.FirstOrDefault(m=>m.ChannelId==command.ChannelId);
            if(map?.Kind!="fan" || string.IsNullOrWhiteSpace(map.RpmSensorId))
                return new {accepted=false,message="Identify a fan output and its RPM sensor before testing it."};
            if(command.CalibrationMode=="response" &&
                (!map.VisuallyIdentified || !map.VerifiedAt.HasValue ||
                    !FanCommissioningRules.HasVerifiedResponse(map)))
                return new {accepted=false,message="Verify the physical fan/RPM pairing at high duty before full calibration."};
            if(command.CalibrationMode=="response" &&
                (command.TemperatureSensorIds?.Length??0)<1)
                return new {accepted=false,message="Select at least one temperature guard for the low-duty response sweep."};
            var profile=_approved.Values.FirstOrDefault(p=>p.ChannelIds.Contains(command.ChannelId));
            if(_overrides.ContainsKey(command.ChannelId))
                return new {accepted=false,message="Return this fan to its curve before calibration."};
            var output=outputs.FirstOrDefault(o=>o.Id==command.ChannelId);
            if(output?.Writable!=true)
                return new {accepted=false,message="The selected PWM output is no longer writable."};
            try
            {
                var minDuty=Math.Max(output.Minimum,profile?.MinimumPercent??10);
                _calibration=new FanAutoCalibration(command.ChannelId,map.RpmSensorId,
                    minDuty,Math.Min(100,output.Maximum),DateTimeOffset.UtcNow,
                    command.CalibrationMode,command.TemperatureSensorIds,
                    command.TemperatureLimit);
                _calibrationFinalized=false;
                _standaloneCalibrationOwned=false;
                _trace?.Event("CALIBRATION",
                    "Authorized "+command.CalibrationMode+" PWM/RPM sweep on "+command.ChannelId+
                    (profile==null?" before curve assignment":" with an approved profile"));
                return new {accepted=true,message="Supervised calibration authorized; observe fans and temperatures until complete."};
            }
            catch(ArgumentException ex)
            {
                return new {accepted=false,message=ex.Message};
            }
        }
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
                if(_calibration?.Running==true &&
                    (command.Action=="stop-all" || _approved.TryGetValue(command.ProfileId,out var toStop)
                        && toStop.ChannelIds.Contains(_calibration.ChannelId)))
                    _calibration.Abort("Approved profile stopped; calibration cancelled.");
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
            if(!_calibrationFinalized && _calibration!=null &&
                configured.ChannelIds.Contains(_calibration.ChannelId))
                return new {accepted=false,message="Complete or cancel the fan calibration before assigning this output to a live profile."};
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
            if (_calibration?.Running==true && _calibration.ExpiresAt<=now)
            {
                _calibration.Abort("Timed out; approved fan curve restored.");
                _trace?.Event("CALIBRATION","Session timed out; restored approved curve.",now);
            }
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
        lock(_sync)
        {
            if(_calibration?.Running==true && now>=_calibration.ExpiresAt)
                _calibration.Abort("Calibration timer expired; restoring the prior controller.");
        }
        Dictionary<string,TemporaryOverride> overrides;
        lock(_sync)
        {
            if(FanResumePolicy.NeedsRearm(previousTick,now,profiles.Length>0))
            {
                _overrides.Clear();
                if(_calibration?.Running==true)
                {
                    _calibration.Abort("Sleep/resume interrupted calibration; approved profile restored.");
                    _trace?.Event("CALIBRATION","Interrupted by sleep/resume; cancelled.",now);
                }
            }
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
                FanAutoCalibration? calibrating;
                lock(_sync)
                {
                    calibrating=_calibration?.Running==true && _calibration.ChannelId==id
                        ?_calibration:null;
                    if(calibrating!=null)
                    {
                        // Never overwrite a specific guard failure with a
                        // generic "cooling demand or mapping changed" message.
                        if(!calibrating.GuardsSafe(frame,now))
                        {
                            _trace?.Event("CALIBRATION",calibrating.Message,now);
                            calibrating=null;
                        }
                        else if(demand.Failsafe)
                        {
                            calibrating.Abort("Calibration stopped because the live "+
                                $"profile '{profile.Name}' entered its 100% cooling failsafe: "+
                                demand.Reason+" Approved fan curve restored.");
                        }
                        else if(settings?.FanChannelMappings?.Any(m=>m.ChannelId==id &&
                            m.Kind=="fan" && m.VisuallyIdentified &&
                            m.RpmSensorId==calibrating.RpmSensorId)!=true)
                        {
                            calibrating.Abort("Fan-to-RPM sensor mapping changed during "+
                                "calibration. Approved fan curve restored.");
                        }
                        else if(demand.Percent>calibrating.CurrentDuty+0.1)
                        {
                            // The user's ordinary profile needs more airflow
                            // than the next calibration speed. Respect that
                            // demand instead of trying to override it.
                            calibrating.FinishPartial(
                                $"Calibration stopped before {calibrating.CurrentDuty}% "+
                                $"because profile '{profile.Name}' currently requires "+
                                $"{demand.Percent:0.#}% PWM. Approved fan curve restored.");
                        }
                        if(calibrating!=null && !calibrating.Running)
                        {
                            _trace?.Event("CALIBRATION",calibrating.Message,now);
                            calibrating=null;
                        }
                    }
                }
                var applied=calibrating!=null
                    ?new FanDutyDecision(calibrating.CurrentDuty,false,"Bounded automatic calibration")
                    :manual==null?demand:
                    new FanDutyDecision(manual.Percent,false,"Temporary manual override");
                if(!controls.TryGetValue(id,out var channel))
                {
                    problems.Add(id+" missing");
                    if(calibrating!=null)
                        lock(_sync)calibrating.Abort("PWM output disappeared; calibration stopped.");
                    _trace?.Observe(profile.Id,id,"missing",demand.Percent,null,false,
                        null,null,null,now);
                    continue;
                }
                // Read back the PREVIOUS tick's duty. A new duty about to
                // be written has not yet reached the Super I/O register.
                // Calibration controls its own feedback and is excluded.
                if(calibrating==null && settings!=null &&
                    settings.FanChannelMappings.Any(m=>m.ChannelId==id && m.Kind=="fan"))
                {
                    (bool Rearm,bool Recovered,string Message) check;
                    lock(_sync)check=_feedback.Check(id,_writer.LastDuty(id),frame,now,
                        settings.FanChannelMappings.FirstOrDefault(m=>
                            m.ChannelId==id && m.Kind=="fan"));
                    if(check.Message.Length>0)
                    {
                        _trace?.Event(check.Rearm?"PWM-DRIFT":
                            check.Recovered?"PWM-RECOVERED":"PWM-FEEDBACK",
                            "channel="+id+" "+check.Message,now);
                        if(check.Rearm || check.Recovered)
                            lock(_sync)_pendingEvents.Enqueue(new FanRecoveryEvent(
                                check.Rearm?"warning":"info","fan:channel:"+id,
                                check.Message,_writer.LastDuty(id)));
                    }
                    if(check.Rearm)_writer.ForceRearm([id]);
                }
                var error=_writer.Apply(id,channel,applied,
                    manual==null && calibrating==null?profile.ResponseSeconds:0,now);
                if(error!=null)
                {
                    problems.Add(error);
                    if(calibrating!=null)lock(_sync)calibrating.Abort(
                        "PWM writer failed; calibration stopped and firmware restoration attempted.");
                }
                if(error==null && calibrating!=null)
                {
                    lock(_sync)
                    {
                        if(calibrating.Running)
                        {
                            if(calibrating.NeedsApply)calibrating.MarkApplied(now);
                            else if(calibrating.Observe(frame,now))
                            {
                                _trace?.Event("CALIBRATION",
                                    "channel="+id+" "+calibrating.Message,now);
                                if(!calibrating.Running)
                                    _pendingEvents.Enqueue(new FanRecoveryEvent(
                                        calibrating.State=="complete"?"info":"warning",
                                        "fan:channel:"+id,calibrating.Message,null));
                            }
                        }
                    }
                }
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
        // Identification and full calibration are deliberately possible
        // before any temperature curve or persistent fan profile exists.
        // In that case, use the same single-threaded approved PWM writer for
        // ONE explicitly elevated, time-bounded channel, then restore firmware.
        // Hold the session lock while deciding and issuing each standalone
        // command. A concurrent admin cancellation cannot race a lower-duty
        // write after the user has already requested restoration.
        lock(_sync)
        {
          var standalone=_calibration?.Running==true &&
              !profiles.Any(p=>p.ChannelIds.Contains(_calibration.ChannelId))
              ?_calibration:null;
          if(standalone!=null)
          {
            var validMapping=settings?.FanChannelMappings?.Any(m=>
                m.ChannelId==standalone.ChannelId && m.Kind=="fan" &&
                m.RpmSensorId==standalone.RpmSensorId)==true;
            if(!validMapping || !standalone.GuardsSafe(frame,now))
                standalone.Abort("Mapping or thermal guard changed; firmware restoration requested.");
            else if(!controls.TryGetValue(standalone.ChannelId,out var output))
                standalone.Abort("PWM output disappeared; firmware restoration requested.");
            else
            {
                var wasApplying=standalone.NeedsApply;
                var error=_writer.Apply(standalone.ChannelId,output,
                    new FanDutyDecision(standalone.CurrentDuty,false,
                        "Supervised pre-curve PWM/RPM calibration"),0,now);
                if(error!=null)
                    standalone.Abort("PWM write failed: "+error);
                else
                {
                    _standaloneCalibrationOwned=true;
                    if(wasApplying)standalone.MarkApplied(now);
                    else standalone.Observe(frame,now);
                }
            }
          }
        }
        // Finalize once after a complete/aborted run. A standalone test must
        // relinquish its physical PWM output, even if settings were changed.
        lock(_sync)
        {
            if(_calibration is {Running:false} ended && !_calibrationFinalized)
            {
                if(_standaloneCalibrationOwned &&
                    !_writer.Release(ended.ChannelId,controls))
                {
                    _pendingReleases.Add(ended.ChannelId);
                    _trace?.Event("CALIBRATION",
                        "Firmware restoration could not be confirmed for "+
                        ended.ChannelId+"; retry queued.",now);
                }
                _standaloneCalibrationOwned=false;
                _calibrationFinalized=true;
                _trace?.Event("CALIBRATION",
                    "channel="+ended.ChannelId+" phase="+ended.Mode+" "+
                    ended.State+"; "+ended.Message,now);
                _pendingEvents.Enqueue(new FanRecoveryEvent(
                    ended.State=="complete"?"info":"warning",
                    "fan:channel:"+ended.ChannelId,ended.Message,null));
            }
            _latest=readouts;
        }
    }

    public void ReleaseBeforeClose(Dictionary<string,IControl> controls) =>
        ReleaseBeforeClose(FanPwmChannelAdapter.Wrap(controls));

    // The same hardware-free release path is exercised by resume regression
    // tests using fake PWM outputs; no real Super I/O writes in CI.
    internal void ReleaseBeforeClose(IReadOnlyDictionary<string,IFanPwmChannel> controls)
    {
        _writer.ReleaseAll(controls);
        _planner.Clear();
        lock(_sync)
        {
            _latest=[];
            _overrides.Clear();
            if(_calibration?.Running==true)
                _calibration.Abort("Hardware reinitialized; calibration cancelled.");
            _feedback.Clear();
            _standaloneCalibrationOwned=false;
            _calibrationFinalized=true;
        }
        _lastTick=null;
        _postResumeRearmDue=null;
        // Persistent approval survives clean shutdown; firmware restore is best-effort.
        // A process crash cannot invoke a software recovery callback.
    }
}
