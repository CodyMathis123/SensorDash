using LibreHardwareMonitor.Hardware;

// Experimental live control is never enabled by persisted settings. Only a
// local elevated, administrator-authorized helper can request a timed session.
// All LHM hardware access stays serialized on SensorWorker's collection thread.
public sealed record FanCommand(
    string Action, string ChannelId = "", string CurveId = "",
    double Percent = 100, int DurationSeconds = 60, bool ConfirmExclusiveOwnership = false,
    string ProfileId = "");

public sealed record FanOutputInfo(string Id, string Name, string Device, string DeviceId,
    float Minimum, float Maximum, string Mode, bool Writable);

public sealed record FanControlStatus(string State, string ChannelId, string CurveId,
    string Mode, double? Target, DateTimeOffset? Expires, string Message);

public sealed class FanControlRuntime
{
    public const int MinExperimentalDuty = 70;
    public const int MaxSessionSeconds = 120;
    private readonly object _sync = new();
    private readonly PersistentFanProfiles _profiles;
    private readonly FanControlTrace _trace;
    public FanControlRuntime(string dataDirectory)
    {
        _trace=new FanControlTrace(dataDirectory);
        _profiles=new PersistentFanProfiles(dataDirectory,_trace);
        _trace.Event("START","Collector fan controller initialized; approved profiles loaded.");
    }
    private IReadOnlyList<FanOutputInfo> _outputs = [];
    private Pending? _pending;
    private Session? _session;
    private bool _firstWritePending;
    private FanControlStatus _status = new("off", "", "", "", null, null,
        "Firmware/default fan control is in charge.");

    private sealed record Pending(FanCommand Command, SimulatedFanCurveConfig? AuthorizedCurve);
    private sealed record Session(string ChannelId, string CurveId, string Mode,
        double ManualDuty, DateTimeOffset Expires, SimulatedFanCurveConfig? AuthorizedCurve);
    // General hardware capability is separate from the conservative duty
    // range used by temporary manual commissioning tests.
    internal static bool SupportsControl(float minimum, float maximum) =>
        float.IsFinite(minimum) && float.IsFinite(maximum) &&
        minimum <= maximum && maximum > 0 && minimum < 100;

    internal static bool SupportsTestRange(float minimum, float maximum) =>
        float.IsFinite(minimum) && float.IsFinite(maximum) && minimum <= maximum &&
        Math.Max((double)minimum, MinExperimentalDuty) <= Math.Min((double)maximum, 100);

    internal static bool CanApplyManualDuty(float minimum, float maximum, double percent) =>
        SupportsTestRange(minimum, maximum) && double.IsFinite(percent) &&
        percent >= Math.Max((double)minimum, MinExperimentalDuty) &&
        percent <= Math.Min((double)maximum, 100);

    public bool IsActive
    {
        get { lock (_sync) return _pending != null || _session != null || _profiles.HasActiveProfiles; }
    }

    public object DebugLog() => _trace.ReadRecent();
    
    public object Snapshot(DashboardSettings? settings = null)
    {
        lock (_sync) return new {
            outputs = _outputs.ToArray(), status = _status,
            minimumDuty = MinExperimentalDuty, maximumSessionSeconds = MaxSessionSeconds,
            requiresAdministrator = true, experimental = true,
            persistent = _profiles.Snapshot(settings),
            diagnostics = new { logPath = _trace.PathName,
                resumeRearms = _trace.ResumeRearms }
        };
    }

    public object Request(FanCommand? command, DashboardSettings settings)
    {
        if(command!=null && command.Action is "start-profile" or "stop-profile" or "stop-all")
        {
            IReadOnlyList<FanOutputInfo> known;
            string? manual;
            lock(_sync) { known = _outputs.ToArray(); manual = _session?.ChannelId; }
            var result=_profiles.Request(command,settings,known,manual);
            if(CoolingEventRecorder.Accepted(result))
                _trace.Event("APPROVED",command.Action+" profile="+command.ProfileId+
                    " (request accepted; hardware application not verified)");
            return result;
        }
        if (command == null || command.Action is not ("arm" or "release"))
            return new { accepted = false, message = "Unsupported fan-control request." };
        lock (_sync)
        {
            if (_pending != null)
                return new { accepted = false, message = "A hardware operation is still pending." };
            if (command.Action == "release")
            {
                _pending = new Pending(command, null);
                _trace.Event("REQUEST","Manual firmware release queued.");
                return new { accepted = true, message = "Firmware-default restoration queued." };
            }
            if (settings.FanChannelMappings?.Any(m => m.ChannelId == command.ChannelId &&
                    m.Kind == "pump") == true)
                return new { accepted = false, message = "This channel is identified as a pump; manual fan-commissioning tests are disabled." };
            if (_session != null)
                return new { accepted = false, message = "Release the current session first." };
            if (!command.ConfirmExclusiveOwnership)
                return new { accepted = false, message = "You must confirm no other software controls this output." };
            if (command.ChannelId.Length is < 3 or > 256 ||
                command.DurationSeconds is < 15 or > MaxSessionSeconds ||
                !double.IsFinite(command.Percent))
                return new { accepted = false, message = "Invalid channel, duration or duty." };
            if(_profiles.OwnsChannel(command.ChannelId))
                return new { accepted=false, message="This output belongs to an active fan profile. Stop the profile before running a manual test." };
            var selected = _outputs.FirstOrDefault(o => o.Id == command.ChannelId);
            if (selected == null || !selected.Writable)
                return new { accepted = false, message = "Selected channel is not a supported writable output." };
            if (command.CurveId.Length == 0 &&
                !CanApplyManualDuty(selected.Minimum, selected.Maximum, command.Percent))
                return new { accepted = false,
                    message = $"Requested duty {command.Percent:0.#}% is outside this channel's supported test range ({Math.Max(selected.Minimum,MinExperimentalDuty):0.#}–{Math.Min(selected.Maximum,100):0.#}%)." };
            var approved = command.CurveId.Length > 0
                ? settings.SimulatedFanCurves.FirstOrDefault(c => c.Id == command.CurveId) : null;
            if (command.CurveId.Length > 0 && approved == null)
                return new { accepted = false, message = "Curve not found in saved settings." };
            // Snapshot the authorized configuration so routine unprivileged
            // settings changes cannot silently alter an active control curve.
            var snapshot = approved == null ? null : new SimulatedFanCurveConfig
            {
                Id = approved.Id, Name = approved.Name,
                SourceSensorId = approved.SourceSensorId,
                MaxSampleAgeSeconds = approved.MaxSampleAgeSeconds,
                Points = approved.Points.Select(p => new CurvePointConfig
                    { Temperature = p.Temperature, Percent = p.Percent }).ToList()
            };
            if (command.CurveId.Length == 0 &&
                (command.Percent < MinExperimentalDuty || command.Percent > 100))
                return new { accepted = false, message = "Experimental manual duty must be 70–100%." };
            _pending = new Pending(command, snapshot);
            _status = new("pending", command.ChannelId, command.CurveId,
                command.CurveId.Length > 0 ? "curve" : "manual", null, null,
                "Waiting for the hardware collection thread.");
            return new { accepted = true, message = "Short live-control session queued." };
        }
    }

    private static Dictionary<string, IControl> FindControls(Computer computer)
    {
        var found = new Dictionary<string, IControl>(StringComparer.Ordinal);
        void Visit(IHardware hardware)
        {
            foreach (var sensor in hardware.Sensors)
                if (sensor.SensorType == SensorType.Control && sensor.Control != null)
                    found[sensor.Identifier.ToString()] = sensor.Control;
            foreach (var child in hardware.SubHardware) Visit(child);
        }
        foreach (var hardware in computer.Hardware) Visit(hardware);
        return found;
    }

    public void Tick(Computer computer, SensorFrame frame, DashboardSettings settings, DateTimeOffset now)
    {
        // Called only from the hardware collection thread.
        var controls = FindControls(computer);
        var listing = new List<FanOutputInfo>();
        void Visit(IHardware hardware)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Control || sensor.Control == null) continue;
                var c = sensor.Control;
                listing.Add(new FanOutputInfo(sensor.Identifier.ToString(), sensor.Name,
                    hardware.Name, hardware.Identifier.ToString(), c.MinSoftwareValue,
                    c.MaxSoftwareValue, c.ControlMode.ToString(),
                    SupportsControl(c.MinSoftwareValue, c.MaxSoftwareValue)));
            }
            foreach (var child in hardware.SubHardware) Visit(child);
        }
        foreach (var hardware in computer.Hardware) Visit(hardware);
        Pending? pending;
        lock (_sync)
        {
            _outputs = listing.OrderBy(o => o.Device).ThenBy(o => o.Name).ToArray();
            pending = _pending;
            _pending = null;
        }
        if (pending != null)
        {
            var request = pending.Command;
            if (request.Action == "release")
                Release(controls, "Released by user; firmware-default restoration requested.");
            else if (_session != null)
                Set(new("refused", request.ChannelId, "", "", null, null, "Another session is active."));
            else if (!controls.TryGetValue(request.ChannelId, out var control))
                Set(new("refused", request.ChannelId, "", "", null, null,
                    "Writable output is no longer available."));
            else if (!SupportsTestRange(control.MinSoftwareValue, control.MaxSoftwareValue))
                Set(new("refused", request.ChannelId, "", "", null, null,
                    $"This output supports {control.MinSoftwareValue:0.#}–{control.MaxSoftwareValue:0.#}% PWM, which does not overlap the safe 70–100% test range."));
            else if (request.CurveId.Length == 0 &&
                     !CanApplyManualDuty(control.MinSoftwareValue, control.MaxSoftwareValue, request.Percent))
                Set(new("refused", request.ChannelId, "", "", null, null,
                    $"Requested {request.Percent:0.#}% is outside the available {Math.Max(control.MinSoftwareValue,MinExperimentalDuty):0.#}–{Math.Min(control.MaxSoftwareValue,100):0.#}% test range."));
            else
            {
                // Undefined is LibreHardwareMonitor's normal initial mode, not
                // an indication that a control is unavailable. Software is
                // also a local LHM state, not evidence another app still owns it.
                _session = new Session(request.ChannelId, request.CurveId,
                    request.CurveId.Length > 0 ? "curve" : "manual",
                    request.Percent, now.AddSeconds(request.DurationSeconds), pending.AuthorizedCurve);
                _firstWritePending = true;
            }
        }
        _profiles.Tick(controls,frame,now,settings);
        if (_session is not { } session) return;
        // Settings can change while a short manual session is active.
        // Revoke manual ownership on the next collector tick if the user
        // identifies this physical channel as a pump.
        if (settings.FanChannelMappings?.Any(m => m.ChannelId == session.ChannelId &&
                m.Kind == "pump") == true)
        {
            Release(controls, "Channel identified as a pump; manual session stopped.");
            return;
        }
        if (now >= session.Expires)
        {
            Release(controls, "Session expired; firmware-default restoration requested.");
            return;
        }
        if (!controls.TryGetValue(session.ChannelId, out var selected))
        {
            Release(controls, "Output disappeared; default restoration attempted.");
            return;
        }
        if (listing.FirstOrDefault(o => o.Id == session.ChannelId) is { } entry &&
            settings.DisabledCollectionDeviceIds.Contains(entry.DeviceId))
        {
            Release(controls, "Output collection was paused; firmware-default restored.");
            return;
        }
        double duty = session.ManualDuty;
        if (session.Mode == "curve")
        {
            var approved = session.AuthorizedCurve;
            var sample = approved == null ? null :
                WorkspaceLogic.Evaluate(frame, new DashboardSettings
                    { SimulatedFanCurves = [approved] }, now).FirstOrDefault();
            if (sample?.State != "simulated" || sample.TargetPercent is not double target ||
                !double.IsFinite(target))
            {
                Release(controls, "Missing/stale curve input; firmware-default restored.");
                return;
            }
            duty = target;
        }
        // Preserve hardware-specific supported PWM bounds, while retaining a
        // conservative commissioning floor for this short test.
        duty = Math.Clamp(duty, Math.Max(MinExperimentalDuty, selected.MinSoftwareValue),
            Math.Min(100, selected.MaxSoftwareValue));
        try
        {
            // An old LHM Software mode/value can be left in local settings.
            // Its setter is a no-op if the new value equals the cached value,
            // so force a fresh control-mode transition on first takeover.
            // This is deliberately confined to the explicitly authorized output.
            if (_firstWritePending && selected.ControlMode == ControlMode.Software)
                selected.SetDefault();
            if (_firstWritePending || selected.ControlMode != ControlMode.Software ||
                Math.Abs(selected.SoftwareValue - duty) >= 0.5)
                selected.SetSoftware((float)duty);
            _firstWritePending = false;
            Set(new("active", session.ChannelId, session.CurveId, session.Mode,
                duty, session.Expires, "Timed experimental control; release to restore default."));
        }
        catch (Exception ex)
        {
            Release(controls, "Hardware write failed (" + ex.GetType().Name + "); restore attempted.");
        }
    }

    // Must be called before the Computer is reinitialized/closed or service exits.
    public void ReleaseBeforeClose(Computer? computer, string reason)
    {
        _trace.Event("STOP",reason);
        Dictionary<string, IControl> channels;
        try { channels = computer == null ? [] : FindControls(computer); }
        catch { channels = []; }
        Release(channels, reason);
        _profiles.ReleaseBeforeClose(channels);
    }

    private void Release(Dictionary<string, IControl> controls, string message)
    {
        var session = _session;
        _session = null;
        _firstWritePending = false;
        if (session == null)
        {
            Set(new("off", "", "", "", null, null, "No SensorDash control session active."));
            return;
        }
        try
        {
            if (!controls.TryGetValue(session.ChannelId, out var control))
                throw new InvalidOperationException("Control object is no longer accessible");
            control.SetDefault();
            Set(new("released", session.ChannelId, session.CurveId, session.Mode,
                null, null, message));
        }
        catch (Exception ex)
        {
            Set(new("restore-unconfirmed", session.ChannelId, session.CurveId, session.Mode,
                null, null, "WARNING: firmware restore could not be verified: " + ex.Message));
        }
    }

    private void Set(FanControlStatus result)
    {
        lock (_sync) _status = result;
    }
}
