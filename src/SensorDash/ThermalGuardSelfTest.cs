// Deterministic, hardware-free regression tests for first-class guard behavior.
internal static class ThermalGuardSelfTest
{
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception("Thermal guard regression: "+message);
    }

    private static SensorFrame Frame(DateTimeOffset when,double? water,double? cpu,
        DateTimeOffset? cpuObserved=null)
    {
        var sensors=new List<SensorReading>();
        if(water.HasValue)sensors.Add(new SensorReading("water","Coolant","NEXT","Controller",
            "Temperature",water,when));
        if(cpu.HasValue)sensors.Add(new SensorReading("cpu","CPU","Ryzen","Cpu",
            "Temperature",cpu,cpuObserved??when));
        return new SensorFrame(when,sensors,null);
    }

    internal static void Run()
    {
        var start=new DateTimeOffset(2026,11,8,12,0,0,TimeSpan.Zero);
        var guard=new ThermalGuardConfig {
            Id="cpu-hot",Name="CPU core",SourceSensorId="cpu",TriggerCelsius=85,
            ReleaseCelsius=80,TriggerAfterSeconds=10,MinimumHoldSeconds=30,
            MaxSampleAgeSeconds=15,MissingInputPolicy="failsafe"
        };
        var profile=new ApprovedFanProfile {
            Id="radiators",MinimumPercent=10,ThermalGuards=[guard],
            Curves=[new SimulatedFanCurveConfig {
                Id="water-curve",SourceSensorId="water",MaxSampleAgeSeconds=15,
                Points=[new(){Temperature=20,Percent=10},
                        new(){Temperature=40,Percent=80}]
            }]
        };
        var planner=new FanProfilePlanner();
        var one=planner.Evaluate(profile,Frame(start,25,84),start);
        Check(!one.Failsafe&&!one.GuardActive&&one.Percent<50,
            "CPU under trigger must not control normal coolant fan curve");
        var hot=planner.Evaluate(profile,Frame(start.AddSeconds(1),25,90),start.AddSeconds(1));
        Check(!hot.GuardActive && hot.Percent<50,"sustained trigger must not fire immediately");
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(10),25,90),start.AddSeconds(10));
        Check(!hot.GuardActive,"trigger duration is measured from first qualifying sample");
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(11),25,90),start.AddSeconds(11));
        Check(hot.GuardActive&&hot.Percent==100&&!hot.Failsafe,
            "sustained overtemperature must force 100% PWM");
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(20),25,75),start.AddSeconds(20));
        Check(hot.GuardActive,"guard must hold after early cooling");
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(42),25,75),start.AddSeconds(42));
        Check(!hot.GuardActive&&hot.Percent<50,
            "below release and after minimum hold must restore coolant curve");
        var missing=planner.Evaluate(profile,Frame(start.AddSeconds(43),25,null),start.AddSeconds(43));
        Check(missing.Failsafe&&missing.Percent==100,
            "required missing component sensor must never be interpreted as cold");
        guard.MissingInputPolicy="warn";
        planner.Reset(profile.Id);
        var warn=planner.Evaluate(profile,Frame(start.AddSeconds(44),25,null),start.AddSeconds(44));
        Check(!warn.Failsafe&&warn.GuardWarning&&warn.Percent<50,
            "optional missing guard should warn without overriding primary coolant curve");
        guard.MissingInputPolicy="failsafe";
        var badPrimary=planner.Evaluate(profile,Frame(start.AddSeconds(45),null,45),start.AddSeconds(45));
        Check(badPrimary.Failsafe&&badPrimary.Percent==100,
            "missing coolant curve must always request maximum");
        planner.Reset(profile.Id);
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(46),25,90),start.AddSeconds(46));
        Check(!hot.GuardActive,"pending latch must not survive reset");
        hot=planner.Evaluate(profile,Frame(start.AddSeconds(47),25,90),start.AddSeconds(47));
        var aged=planner.Evaluate(profile,Frame(start.AddSeconds(50),25,90,
            start.AddSeconds(20)),start.AddSeconds(50));
        Check(aged.Failsafe&&aged.Percent==100,"stale critical sensor must fail safe");
    }
}
