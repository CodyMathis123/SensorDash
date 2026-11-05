// No physical hardware required: exercise the feedback and calibration engines
// with synthetic register readback, tachometer samples, and approved profiles.
internal static class FanFeedbackCalibrationSelfTest
{
    private sealed class FakePwm : IFanPwmChannel
    {
        public double Minimum => 0;
        public double Maximum => 100;
        public bool IsSoftwareMode { get; private set; } = true;
        public double SoftwareValue { get; private set; } = 10;
        public int Writes { get; private set; }
        public int Resets { get; private set; }
        public void SetSoftware(double duty)
        {
            SoftwareValue=duty;
            IsSoftwareMode=true;
            Writes++;
        }
        public void SetDefault(){IsSoftwareMode=false;Resets++;}
    }
    private static void Check(bool valid,string reason)
    {
        if(!valid)throw new Exception("Fan feedback/calibration regression: "+reason);
    }
    private static SensorFrame Frame(DateTimeOffset at,double? pwm=10,
        double? rpm=490,double? temperature=30)
    {
        var readings=new List<SensorReading>();
        if(pwm.HasValue)readings.Add(new SensorReading("pwm","PWM","IT8688E",
            "SuperIO","Control",pwm.Value,at));
        if(rpm.HasValue)readings.Add(new SensorReading("rpm","Fan tach","IT8688E",
            "SuperIO","Fan",rpm.Value,at));
        if(temperature.HasValue)readings.Add(new SensorReading("temp","CPU temp",
            "Ryzen","Cpu","Temperature",temperature.Value,at));
        return new SensorFrame(at,readings,null);
    }
    internal static void Run()
    {
        var now=DateTimeOffset.UtcNow;
        var monitor=new FanPwmFeedbackMonitor();
        Check(!monitor.Check("pwm",10,Frame(now),now).Rearm,
            "matching register should not cause reset");
        Check(monitor.Check("pwm",10,Frame(now.AddSeconds(60),53),
            now.AddSeconds(60)).Rearm,
            "first registered 43 percent mismatch should request rearm");
        Check(!monitor.Check("pwm",10,Frame(now.AddSeconds(120),53),
            now.AddSeconds(120)).Rearm,
            "rearm must be rate-limited after mismatch");
        Check(monitor.Check("pwm",10,Frame(now.AddSeconds(180),10),
            now.AddSeconds(180)).Recovered,
            "readback recovery should be surfaced");
        Check(!monitor.Check("pwm",10,Frame(now.AddSeconds(240),null),
            now.AddSeconds(240)).Rearm,
            "missing register feedback must never force a write");
        Check(monitor.Snapshot()["pwm"].State=="unavailable",
            "missing feedback is unknown, not a good PWM match");

        var sweep=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(sweep.StepCount>=7 && sweep.CurrentDuty==100,
            "bounded auto sweep should start high and include approved minimum");
        sweep.MarkApplied(now);
        Check(!sweep.GuardsSafe(Frame(now,10,490,95),now) &&
            sweep.State=="aborted",
            "temperature guard must abort a low-duty sweep before commanding hardware");
        sweep=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        sweep.MarkApplied(now);
        Check(!sweep.Observe(Frame(now.AddSeconds(2),100,1960),now.AddSeconds(2)),
            "no calibration samples before settling time");
        for(var second=6;second<=8;second++)
            sweep.Observe(Frame(now.AddSeconds(second),100,1960),
                now.AddSeconds(second));
        Check(sweep.CompletedSteps==1 && sweep.CurrentDuty==85 &&
            sweep.NeedsApply,"stable fresh RPM should advance to the next step");
        sweep.MarkApplied(now.AddSeconds(9));
        Check(sweep.Observe(Frame(now.AddSeconds(16),85,0),now.AddSeconds(16)) &&
            sweep.State=="aborted","stalled fan aborts calibration");
        Check(sweep.Measurements.Count==1,
            "aborted calibration retains observations for diagnostics only");
        var timeout=new FanAutoCalibration("pwm","rpm",70,100,now);
        timeout.MarkApplied(now);
        Check(timeout.Observe(Frame(now.AddSeconds(23),10,1600),
            now.AddSeconds(23)) && timeout.State=="aborted",
            "register mismatch must abort instead of saving corrupted data");

        var dir=Directory.CreateTempSubdirectory("SensorDash-Calibration-");
        try
        {
            var settings=new DashboardSettings{
                SimulatedFanCurves=[new SimulatedFanCurveConfig{
                    Id="cpu",Name="CPU",SourceSensorId="temp",MaxSampleAgeSeconds=60,
                    Points=[new(){Temperature=30,Percent=10},
                        new(){Temperature=80,Percent=100}]
                }],
                FanProfiles=[new FanProfileConfig{
                    Id="radiator",Name="Radiators",CurveIds=["cpu"],
                    ChannelIds=["pwm"],MinimumPercent=10,
                    ResponseSeconds=10
                }],
                FanChannelMappings=[new FanChannelMapping{
                    ChannelId="pwm",Label="Radiator fans",Kind="fan",
                    RpmSensorId="rpm",VisuallyIdentified=true
                }]
            };
            WorkspaceLogic.Normalize(settings);
            var outputs=new List<FanOutputInfo>{
                new("pwm","PWM output","IT8688E","superio",0,100,"Software",true)
            };
            var profiles=new PersistentFanProfiles(dir.FullName);
            Check(CoolingEventRecorder.Accepted(profiles.Request(
                new FanCommand("start-profile",ProfileId:"radiator",
                    ConfirmExclusiveOwnership:true),settings,outputs,null)),
                "approved profile must be active before calibration");
            Check(CoolingEventRecorder.Accepted(profiles.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    ConfirmExclusiveOwnership:true),settings,outputs)),
                "privileged calibration approved for known fan and tach");
            var fake=new FakePwm();
            var channels=new Dictionary<string,IFanPwmChannel>{{"pwm",fake}};
            profiles.Tick(channels,Frame(now,10,490),now,settings);
            Check(fake.SoftwareValue==100 && fake.Writes>0,
                "calibration first step is applied only through the approved profile writer");
            profiles.Tick(channels,Frame(now.AddSeconds(2),100,1980),
                now.AddSeconds(2),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(6),100,1980),
                now.AddSeconds(6),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(7),100,1980),
                now.AddSeconds(7),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(8),100,1980),
                now.AddSeconds(8),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(9),100,1980),
                now.AddSeconds(9),settings);
            Check(fake.SoftwareValue==85,
                "calibration walks to 85 percent after stable 100 percent RPM");
            profiles.Tick(channels,Frame(now.AddSeconds(10),85,1650,80),
                now.AddSeconds(10),settings);
            Check(fake.SoftwareValue>=99,
                "high temperature demand aborts lower calibration duty and invokes failsafe");
            var snapshot=System.Text.Json.JsonSerializer.Serialize(profiles.Snapshot(settings));
            Check(snapshot.Contains("aborted"),
                "calibration abort visible in read-only runtime status");
            var pumpSettings=new DashboardSettings {
                FanChannelMappings=[new FanChannelMapping{
                    ChannelId="pwm",Kind="pump",RpmSensorId="rpm",
                    VisuallyIdentified=true
                }]
            };
            Check(!CoolingEventRecorder.Accepted(profiles.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    ConfirmExclusiveOwnership:true),pumpSettings,outputs)),
                "pump mapping must block automatic PWM calibration");
        }
        finally { try{dir.Delete(true);}catch{} }
    }
}
