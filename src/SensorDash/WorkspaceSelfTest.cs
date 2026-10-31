// Executable integration smoke checks for the deterministic curve/virtual-sensor
// engine. Does not open hardware or call any fan control APIs.
internal static class WorkspaceSelfTest
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = new DashboardSettings
        {
            SimulatedFanCurves =
            [
                new SimulatedFanCurveConfig
                {
                    Id = "curve-test", Name = "CPU temperature curve", SourceSensorId = "cpu",
                    Points = [new() { Temperature = 30, Percent = 25 },
                              new() { Temperature = 50, Percent = 55 },
                              new() { Temperature = 75, Percent = 100 }]
                }
            ]
        };
        WorkspaceLogic.Normalize(cfg);
        var frame = new SensorFrame(now,
        [
            new SensorReading("cpu", "CPU", "Processor", "Cpu", "Temperature", 55, now),
            new SensorReading("gpu", "GPU", "Video card", "Gpu", "Temperature", 40, now)
        ], null);
        static void Check(double? expected, SimulatedFanCurveReading curve, string label)
        {
            if ((expected == null) != (curve.TargetPercent == null) ||
                (expected is double value &&
                 Math.Abs(value - curve.TargetPercent!.Value) > .001))
                throw new Exception(label + " did not produce expected simulation target");
        }
        var sample = WorkspaceLogic.Evaluate(frame, cfg, now)[0];
        Check(64, sample, "Linear interpolation");
        if (sample.State != "simulated") throw new Exception("Expected simulated state");
        frame.Sensors.RemoveAll(s => s.Id == "cpu");
        Check(null, WorkspaceLogic.Evaluate(frame, cfg, now)[0], "Missing input");
        frame.Sensors.Add(new SensorReading("cpu", "CPU",
            "Processor", "Cpu", "Temperature", 55, now.AddMinutes(-2)));
        var stale = WorkspaceLogic.Evaluate(frame, cfg, now)[0];
        Check(null, stale, "Stale input");
        if (stale.State != "stale-input") throw new Exception("Stale input state missing");
        frame.Sensors[^1] = frame.Sensors[^1] with { ObservedAt = now, Value = 5 };
        Check(25, WorkspaceLogic.Evaluate(frame, cfg, now)[0], "Clamped low point");
        frame.Sensors[^1] = frame.Sensors[^1] with { Value = 95 };
        Check(100, WorkspaceLogic.Evaluate(frame, cfg, now)[0], "Clamped high point");
        frame.Sensors[^1] = frame.Sensors[^1] with { Value = 46 };
        cfg.TrayWidgets = Enumerable.Range(0,8).Select(i=>new TrayWidgetConfig {
            Id="t"+i, SensorId="temperature-"+i
        }).ToList();
        // Cooling systems define meaningful component roles, not passive
        // radiators. Associations must participate in lean history references.
        cfg.CoolingSystems = [new CoolingSystemConfig {
            Id = "loop-a", Name = "Primary cooling system", Kind = "liquid",
            Components = [
                new CoolingComponentConfig {
                    Id = "heat", Name = "GPU heat source", Kind = "heat-source",
                    SensorIds = ["gpu", "gpu-power"] },
                new CoolingComponentConfig {
                    Id = "fans", Name = "Fan bank", Kind = "fan-group",
                    SensorIds = ["fan-rpm", "fan-duty"] },
                new CoolingComponentConfig {
                    Id = "probe", Name = "Coolant sensor", Kind = "temperature-probe",
                    SensorIds = ["coolant-in"] },
                new CoolingComponentConfig {
                    Id = "invalid", Name = "Invalid type", Kind = "old-radiator",
                    SensorIds = [] }
            ]
        }];
        WorkspaceLogic.Normalize(cfg);
        if (cfg.CoolingSystems[0].Components[1].Kind != "fan-group" ||
            cfg.CoolingSystems[0].Components[2].Kind != "temperature-probe" ||
            cfg.CoolingSystems[0].Components[3].Kind != "other")
            throw new Exception("Cooling component roles were not validated");
        var referenced = WorkspaceLogic.ReferencedSensorIds(cfg);
        if (!referenced.Contains("gpu-power") ||
            !referenced.Contains("fan-rpm") ||
            !referenced.Contains("coolant-in") ||
            !referenced.Contains("cpu"))
            throw new Exception("Cooling system sensor associations were not included in in-use history");

        // Hide is presentation-only; it must not change database eligibility.
        cfg.HiddenSensorIds = ["cpu"];
        cfg.HiddenDeviceIds = ["network-adapter"];
        cfg.HiddenHardwareTypes = ["Network"];
        var cpuSample = new SensorReading("cpu", "CPU Package", "CPU", "Cpu",
            "Temperature", 46, now, "cpu-hardware");
        var nicSample = new SensorReading("nic", "Transfer", "Ethernet", "Network",
            "Throughput", 350, now, "network-adapter");
        if (!HistoryStore.ShouldRecord(cpuSample, cfg))
            throw new Exception("Hiding a sensor must not disable its historical samples");
        cfg.HistoryMode = "in-use";
        WorkspaceLogic.Normalize(cfg);
        if (!HistoryStore.ShouldRecord(cpuSample, cfg) ||
            HistoryStore.ShouldRecord(nicSample, cfg))
            throw new Exception("Only-in-use history mode must honor workspace references");
        cfg.HistoryMode = "all";
        cfg.ExcludedHistoryHardwareTypes = ["Network"];
        WorkspaceLogic.Normalize(cfg);
        if (HistoryStore.ShouldRecord(nicSample, cfg) ||
            !HistoryStore.ShouldRecord(cpuSample, cfg))
            throw new Exception("Network history exclusion must not affect other devices");
        cfg.HistoryMode = "balanced";
        var drive = new SensorReading("/hdd/0/level/0", "Disk Life",
            "NVMe Drive", "Storage", "Level", 98, now);
        if (!HistoryStore.ShouldRecord(drive, cfg))
            throw new Exception("Storage data must be archived for the Pi dashboard");
        cfg.HistoryMode = "nonsense";
        WorkspaceLogic.Normalize(cfg);
        if (cfg.HistoryMode != "balanced")
            throw new Exception("Unknown history policy was not normalized");

        // Validate the complete generic collection-switch model without
        // opening any hardware or creating an administrator process.
        cfg.DisabledCollectionGroups = ["Network", "Storage", "NotARealGroup"];
        cfg.DisabledCollectionDeviceIds = ["device-0"];
        WorkspaceLogic.Normalize(cfg);
        if (cfg.DisabledCollectionGroups.Count != 2 ||
            cfg.DisabledCollectionGroups.Contains("NotARealGroup") ||
            cfg.DisabledCollectionDeviceIds.Count != 1)
            throw new Exception("Hardware collection config normalization failed");
        if (HardwareCollection.Enabled(cfg, "Network") ||
            HardwareCollection.Enabled(cfg, "Storage") ||
            !HardwareCollection.Enabled(cfg, "Cpu"))
            throw new Exception("Collection family enabled state was incorrect");
        var disabledSignature = HardwareCollection.Signature(cfg);
        var computer = HardwareCollection.CreateComputer(cfg);
        if (computer.IsNetworkEnabled || computer.IsStorageEnabled ||
            !computer.IsCpuEnabled || !computer.IsMotherboardEnabled)
            throw new Exception("LHM computer switches did not match collection settings");
        cfg.DisabledCollectionGroups = ["Gpu"];
        WorkspaceLogic.Normalize(cfg);
        if (HardwareCollection.Signature(cfg) == disabledSignature)
            throw new Exception("A changed hardware family must reopen collector discovery");
        if (HardwareCollection.Groups.Length != 8)
            throw new Exception("The complete list of LHM hardware collection families is missing");

        // Windows notification icons must remain legible when rasterized at 16px.
        if (TraySensorIcon.Compact("24.0", 16) != "24"
            || TraySensorIcon.Compact("24.0", 24) != "24.0"
            || TraySensorIcon.Compact("1340", 16) != "1k"
            || TraySensorIcon.Compact("--", 16) != "--")
            throw new Exception("Tiny tray-icon number formatting is not readable");
        using (var icon = TraySensorIcon.Create("42", "#ff4040"))
        {
            if (icon.Width < 16 || icon.Height < 16)
                throw new Exception("Tray icon could not be generated");
        }
        // Read-only workspace evaluation is independent of the experimental
        // privileged controller. A new runtime must never own outputs by default.
        var live = new FanControlRuntime(Path.Combine(Path.GetTempPath(),
            "SensorDash-Fan-Test-" + Guid.NewGuid().ToString("N")));
        var snapshot = System.Text.Json.JsonSerializer.Serialize(live.Snapshot(),
            System.Text.Json.JsonSerializerOptions.Web);
        if (!snapshot.Contains("\"state\":\"off\"", StringComparison.Ordinal))
            throw new Exception("Fan control was activated without authorization");
        var rejected = System.Text.Json.JsonSerializer.Serialize(
            live.Request(new FanCommand("arm", "fake-channel", "", 30, 60, false), cfg),
            System.Text.Json.JsonSerializerOptions.Web);
        if (!rejected.Contains("\"accepted\":false", StringComparison.Ordinal))
            throw new Exception("Unconfirmed fan control request was accepted");
        rejected = System.Text.Json.JsonSerializer.Serialize(
            live.Request(new FanCommand("arm", "fake-channel", "", 100, 60, true), cfg),
            System.Text.Json.JsonSerializerOptions.Web);
        if (!rejected.Contains("\"accepted\":false", StringComparison.Ordinal))
            throw new Exception("Unknown fan output was accepted");
        if (FanControlRuntime.MinExperimentalDuty < 70 ||
            FanControlRuntime.MaxSessionSeconds > 120)
            throw new Exception("Experimental control limits changed without safety review");
        // Approval is the privilege boundary: test persistence without
        // creating IControl, opening hardware, or changing physical outputs.
        var persistentTestDir=Directory.CreateTempSubdirectory("SensorDashFanTests-");
        try
        {
            var profileCurve=new SimulatedFanCurveConfig {
                Id="test-cpu-curve", Name="CPU curve", SourceSensorId="test-cpu-temp",
                Points=[new(){Temperature=40,Percent=20},new(){Temperature=85,Percent=90}]
            };
            var profileSettings=new DashboardSettings {
                FanProfiles=[new FanProfileConfig {
                    Id="test-rads",Name="Test radiators",CurveIds=["test-cpu-curve"],
                    ChannelIds=["/test/control/1"],MinimumPercent=15,
                    HysteresisDegrees=3,ResponseSeconds=10
                }],
                SimulatedFanCurves=[profileCurve]
            };
            WorkspaceLogic.Normalize(profileSettings);
            var advertised=new List<FanOutputInfo>{
                new("/test/control/1","Test fan","Test mainboard","/test/mainboard",
                    0,100,"Undefined",true)
            };
            var p=new PersistentFanProfiles(persistentTestDir.FullName);
            var acceptedJson=System.Text.Json.JsonSerializer.Serialize(
                p.Request(new FanCommand("start-profile",ProfileId:"test-rads",
                    ConfirmExclusiveOwnership:true),profileSettings,advertised,null),
                System.Text.Json.JsonSerializerOptions.Web);
            if(!acceptedJson.Contains("\"accepted\":true",StringComparison.Ordinal))
                throw new Exception("Admin-approved profile was not accepted");
            if(!p.OwnsChannel("/test/control/1")||!p.HasActiveProfiles)
                throw new Exception("Approved fan output was not retained");
            var restarted=new PersistentFanProfiles(persistentTestDir.FullName);
            if(!restarted.OwnsChannel("/test/control/1"))
                throw new Exception("Approved profile did not survive collector restart");
            var stoppedJson=System.Text.Json.JsonSerializer.Serialize(
                restarted.Request(new FanCommand("stop-profile",ProfileId:"test-rads"),
                    profileSettings,advertised,null),
                System.Text.Json.JsonSerializerOptions.Web);
            if(!stoppedJson.Contains("\"accepted\":true",StringComparison.Ordinal))
                throw new Exception("Admin stop of fan profile rejected");
            if(new PersistentFanProfiles(persistentTestDir.FullName).HasActiveProfiles)
                throw new Exception("Stopping fan profile failed to revoke persistence");
            acceptedJson=System.Text.Json.JsonSerializer.Serialize(
                p.Request(new FanCommand("start-profile",ProfileId:"test-rads",
                    ConfirmExclusiveOwnership:false),profileSettings,advertised,null),
                System.Text.Json.JsonSerializerOptions.Web);
            if(!acceptedJson.Contains("\"accepted\":false",StringComparison.Ordinal))
                throw new Exception("Unconfirmed persistent fan profile was accepted");
        }
        finally
        {
            try{persistentTestDir.Delete(true);}catch{}
        }

        // LHM reports Undefined on first instantiation, not firmware state.
        // Hardware eligibility should depend on the supported output range,
        // rather than which mode the LHM wrapper happens to remember.
        if (!FanControlRuntime.SupportsTestRange(0,100) ||
            !FanControlRuntime.SupportsTestRange(80,90) ||
            FanControlRuntime.SupportsTestRange(0,50) ||
            FanControlRuntime.CanApplyManualDuty(80,90,70) ||
            !FanControlRuntime.CanApplyManualDuty(80,90,85) ||
            FanControlRuntime.CanApplyManualDuty(80,90,100))
            throw new Exception("Fan control per-channel PWM capability detection incorrect");

    }
}
