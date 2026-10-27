// Executable integration smoke checks for the deterministic curve/virtual-sensor
// engine. Does not open hardware or call any fan control APIs.
internal static class WorkspaceSelfTest
{
    public static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = new DashboardSettings {
            VirtualSensors = [
                new VirtualSensorConfig { Id="virtual:max-test", Name="Max temperature",
                    Operation="max", SensorIds=["cpu","gpu"] }
            ],
            SimulatedFanCurves = [
                new SimulatedFanCurveConfig {
                    Id="curve-test", Name="Test curve", SourceSensorId="virtual:max-test",
                    Points=[new(){Temperature=30,Percent=25},new(){Temperature=50,Percent=55},
                            new(){Temperature=75,Percent=100}]
                }
            ]
        };
        WorkspaceLogic.Normalize(cfg);
        var frame = new SensorFrame(now, [
            new SensorReading("cpu","CPU","Mainboard","Cpu","Temperature",40,now),
            new SensorReading("gpu","GPU","Video card","Gpu","Temperature",55,now)
        ],null);
        var virtuals = WorkspaceLogic.VirtualReadings(frame,cfg.VirtualSensors);
        if(virtuals.Count!=1||virtuals[0].Id!="virtual:max-test"||
            Math.Abs(virtuals[0].Value!.Value-55)>0.0001)
            throw new Exception("Virtual Max must select highest physical temperature");
        frame.Sensors.AddRange(virtuals);

        static void Check(double? expected,SimulatedFanCurveReading curve,string label)
        {
            if((expected==null)!=(curve.TargetPercent==null)||
                (expected!=null&&Math.Abs(expected.Value-curve.TargetPercent!.Value)>.001))
                throw new Exception(label+" did not produce expected simulation target");
        }
        var sample=WorkspaceLogic.Evaluate(frame,cfg,now)[0];
        // 55 lies one-fifth along the 50->75 segment (55% -> 100%).
        Check(64,sample,"Linear interpolation");
        if(sample.State!="simulated")throw new Exception("Expected simulated state");
        frame.Sensors.RemoveAll(s=>s.Id=="virtual:max-test");
        Check(null,WorkspaceLogic.Evaluate(frame,cfg,now)[0],"Missing input");
        frame.Sensors.Add(new SensorReading("virtual:max-test","Max temperature",
            "Virtual sensors","Virtual","Temperature",55,now.AddMinutes(-2)));
        var stale=WorkspaceLogic.Evaluate(frame,cfg,now)[0];
        Check(null,stale,"Stale input");
        if(stale.State!="stale-input")throw new Exception("Stale input state missing");
        frame.Sensors[^1]=frame.Sensors[^1] with {ObservedAt=now,Value=5};
        Check(25,WorkspaceLogic.Evaluate(frame,cfg,now)[0],"Clamped low point");
        frame.Sensors[^1]=frame.Sensors[^1] with {Value=95};
        Check(100,WorkspaceLogic.Evaluate(frame,cfg,now)[0],"Clamped high point");
        cfg.TrayWidgets = Enumerable.Range(0,8).Select(i=>new TrayWidgetConfig {
            Id="t"+i, SensorId="temperature-"+i
        }).ToList();
        cfg.Dashboards = [new WorkspaceDashboardConfig { Id="one",Name="Example",
            Widgets=[new WorkspaceWidgetConfig{Id="widget",Title="Temperature"}]}];
        WorkspaceLogic.Normalize(cfg);
        if(cfg.TrayWidgets.Count!=6)throw new Exception("Tray widget limit not enforced");
        if(cfg.Dashboards[0].Widgets.Count!=1)throw new Exception("Dashboard configuration lost");
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
        cfg.Dashboards[0].Locked = true;
        cfg.Dashboards[0].Widgets[0].SensorIds = ["cpu"];
        WorkspaceLogic.Normalize(cfg);
        if (!cfg.Dashboards[0].Locked || !cfg.HiddenSensorIds.Contains("cpu"))
            throw new Exception("Dashboard lock or sensor visibility settings were lost");
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
        var live = new FanControlRuntime();
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
    }
}
