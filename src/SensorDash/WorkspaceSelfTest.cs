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
        // The simulation engine makes no reference to ISensor.Control or IControl.
        // The absence of any write entry point remains an intentional safety boundary.
    }
}
