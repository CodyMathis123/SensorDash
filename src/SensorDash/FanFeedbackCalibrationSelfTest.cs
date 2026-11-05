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
        // The original sleep/resume failure might show 10% register
        // readback with ~1269 RPM instead of the calibrated ~490 RPM.
        // A one-minute stable-duty comparison catches that second failure mode.
        var tachMonitor=new FanPwmFeedbackMonitor();
        var verified=new FanChannelMapping{
            ChannelId="pwm",Kind="fan",RpmSensorId="rpm",
            VisuallyIdentified=true,VerifiedAt=now,
            Measurements=[
                new(){DutyPercent=10,Rpm=490,ObservedAt=now},
                new(){DutyPercent=40,Rpm=1080,ObservedAt=now},
                new(){DutyPercent=100,Rpm=1980,ObservedAt=now}
            ]
        };
        Check(!tachMonitor.Check("pwm",10,Frame(now,10,1269),now,verified).Rearm,
            "do not infer a settled tach mismatch immediately after startup");
        var drift=tachMonitor.Check("pwm",10,Frame(now.AddSeconds(60),10,1269),
            now.AddSeconds(60),verified);
        Check(drift.Rearm&&drift.Message.Contains("Calibrated RPM mismatch"),
            "calibrated RPM detects bad physical fan speed despite matching PWM register");
        Check(!tachMonitor.Check("pwm",10,Frame(now.AddSeconds(120),10,1269),
            now.AddSeconds(120),verified).Rearm,
            "rearm must remain bounded for persistent calibrated RPM deviation");
        Check(tachMonitor.Check("pwm",10,Frame(now.AddSeconds(180),10,490),
            now.AddSeconds(180),verified).Recovered,
            "normal RPM after correction generates a recovery transition");

        var sweep=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(sweep.StepCount==19 && sweep.CurrentDuty==100 &&
            sweep.ExpiresAt==now.AddSeconds(1500),
            "full response calibration must cover 100 through 10 in 5 percent steps with a 25-minute cap");
        Check(new FanAutoCalibration("pwm","rpm",70,100,now).StepCount==3,
            "high-duty identification remains a short separate three-point test");
        Check(new FanAutoCalibration("pwm","rpm",30,100,now,
            "response",["temp"],85).StepCount==15,
            "response sweep never tests below the selected channel minimum");
        sweep.MarkApplied(now);
        Check(!sweep.GuardsSafe(Frame(now,10,490,95),now) &&
            sweep.State=="aborted" &&
            sweep.Message.Contains("CPU temp") &&
            sweep.Message.Contains("95.0°C") &&
            sweep.Message.Contains("85.0°C"),
            "over-limit temperature must identify the exact sensor, value and threshold");
        // The readable guard name is taken from the user's alias, not
        // LibreHardwareMonitor's raw sensor label. Sensor IDs still make
        // missing-sensor messages unambiguous.
        var aliases=new Dictionary<string,string>(StringComparer.Ordinal){
            ["temp"]="CPU Coolant Guard"
        };
        var namedHot=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85,aliases);
        Check(!namedHot.GuardsSafe(Frame(now,10,490,95),now) &&
            namedHot.Message.Contains("CPU Coolant Guard") &&
            !namedHot.Message.Contains("CPU temp"),
            "thermal guard over-temperature diagnostics must honor configured sensor aliases");
        var namedMissing=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85,aliases);
        Check(!namedMissing.GuardsSafe(Frame(now,10,490,null),now) &&
            namedMissing.Message.Contains("CPU Coolant Guard (temp)"),
            "missing aliased thermal guard must retain its stable sensor ID");
        var namedSamples=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85,aliases);
        namedSamples.MarkApplied(now);
        for(var second=8;second<=20;second++)
            namedSamples.Observe(Frame(now.AddSeconds(second),100,1960),
                now.AddSeconds(second));
        Check(namedSamples.CompletedSteps==1 &&
            namedSamples.Measurements[0].Temperatures.Single().SensorName=="CPU Coolant Guard",
            "captured calibration guard temperatures must use the selected alias");

        var missingGuard=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(!missingGuard.GuardsSafe(Frame(now,10,490,null),now) &&
            missingGuard.Message.Contains("CPU") == false &&
            missingGuard.Message.Contains("(temp)") &&
            missingGuard.Message.Contains("not found"),
            "missing selected sensor must be identified by its saved sensor ID");
        var staleGuard=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(staleGuard.GuardsSafe(Frame(now,10,490,30),now.AddSeconds(8)),
            "a short sensor collection delay should not abort a 19-step calibration");
        Check(!staleGuard.GuardsSafe(Frame(now,10,490,30),now.AddSeconds(11)) &&
            staleGuard.Message.Contains("CPU temp") &&
            staleGuard.Message.Contains("11.0s ago") &&
            staleGuard.Message.Contains("30.0°C"),
            "stale guard must state the actual sample age and last temperature");
        var risingGuard=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(risingGuard.GuardsSafe(Frame(now,10,490,30),now) &&
            !risingGuard.GuardsSafe(Frame(now.AddSeconds(1),10,490,38),
                now.AddSeconds(1)) &&
            risingGuard.Message.Contains("CPU temp") &&
            risingGuard.Message.Contains("30.0°C") &&
            risingGuard.Message.Contains("38.0°C"),
            "temperature-rise abort must identify the sensor and its measured increase");
        var invalidGuard=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        Check(!invalidGuard.GuardsSafe(Frame(now,10,490,double.NaN),now) &&
            invalidGuard.Message.Contains("no valid temperature"),
            "invalid temperature must never be confused with overheating");
        sweep=new FanAutoCalibration("pwm","rpm",10,100,now,
            "response",["temp"],85);
        sweep.MarkApplied(now);
        Check(!sweep.Observe(Frame(now.AddSeconds(2),100,1960),now.AddSeconds(2)),
            "no calibration samples before settling time");
        // A short plateau must NOT be counted as a settled fan speed.
        for(var second=8;second<=13;second++)
            sweep.Observe(Frame(now.AddSeconds(second),100,1960),
                now.AddSeconds(second));
        Check(sweep.CompletedSteps==0,
            "Six readings over a short interval must not complete a calibration step");
        for(var second=14;second<=20;second++)
            sweep.Observe(Frame(now.AddSeconds(second),100,1960),
                now.AddSeconds(second));
        Check(sweep.CompletedSteps==1 && sweep.CurrentDuty==95 &&
            sweep.NeedsApply,"full response sweep advances in five percent increments");
        Check(sweep.Measurements[0].Temperatures.Count==1 &&
              sweep.Measurements[0].Temperatures[0].SensorId=="temp" &&
              Math.Abs(sweep.Measurements[0].Temperatures[0].Celsius-30)<0.01,
            "each settled fan RPM point must capture the selected thermal guard's actual reading");
        sweep.MarkApplied(now.AddSeconds(21));
        Check(sweep.Observe(Frame(now.AddSeconds(28),95,0),now.AddSeconds(28)) &&
            sweep.State=="aborted","stalled fan aborts calibration");
        Check(sweep.Measurements.Count==1,
            "aborted calibration retains observations for diagnostics only");

        // A smoothly increasing RPM is not a settled response even when
        // adjacent samples are individually close together.
        var rising=new FanAutoCalibration("pwm","rpm",70,100,now);
        rising.MarkApplied(now);
        for(var second=8;second<=32;second++)
            rising.Observe(Frame(now.AddSeconds(second),100,1700+
                (second-8)*12),now.AddSeconds(second));
        Check(rising.CompletedSteps==0 && rising.Stability=="rising",
            "monotonic accelerating fan must not be saved as calibrated RPM");
        for(var second=33;second<=51;second++)
            rising.Observe(Frame(now.AddSeconds(second),100,1988+
                (second%3==0?4:second%3==1?-3:1)),now.AddSeconds(second));
        Check(rising.CompletedSteps==1 && rising.Measurements[0].Rpm>1950,
            "fan may be accepted later when the whole window has stabilized");
        var falling=new FanAutoCalibration("pwm","rpm",70,100,now);
        falling.MarkApplied(now);
        for(var second=8;second<=32;second++)
            falling.Observe(Frame(now.AddSeconds(second),100,1980-
                (second-8)*12),now.AddSeconds(second));
        Check(falling.CompletedSteps==0 && falling.Stability=="falling",
            "monotonically decelerating fan must not be saved as calibrated RPM");
        for(var second=33;second<=51;second++)
            falling.Observe(Frame(now.AddSeconds(second),100,1690+
                (second%3==0?4:second%3==1?-3:1)),now.AddSeconds(second));
        Check(falling.CompletedSteps==1,
            "falling RPM must settle before calibration accepts the sample");
        // A fan that never settles must time out rather than guessing.
        var unstable=new FanAutoCalibration("pwm","rpm",70,100,now);
        unstable.MarkApplied(now);
        for(var second=8;second<=60;second++)
            unstable.Observe(Frame(now.AddSeconds(second),100,
                second%2==0?1300:1550),now.AddSeconds(second));
        Check(unstable.State=="aborted" && unstable.Measurements.Count==0,
            "unstable fan must time out without recording any fake calibration");

        var timeout=new FanAutoCalibration("pwm","rpm",70,100,now);
        timeout.MarkApplied(now);
        Check(timeout.Observe(Frame(now.AddSeconds(61),10,1600),
            now.AddSeconds(61)) && timeout.State=="aborted",
            "register mismatch must abort instead of saving corrupted data");

        // Retaining a full 19-point response in settings is as important as
        // gathering it; normalization previously discarded points after 12.
        var points=Enumerable.Range(0,19).Select(i=>new FanCalibrationPoint{
            DutyPercent=100-i*5,
            Rpm=2000-i*70,ObservedAt=now
        }).ToList();
        points[0].Temperatures=[
            new(){SensorId="nvme",SensorName="NVMe Temperature",
                Celsius=47.5,ObservedAt=now},
            new(){SensorId="bad",SensorName="Invalid",
                Celsius=double.NaN,ObservedAt=now}
        ];
        var allPoints=new FanChannelMapping {
            ChannelId="pwm-all",Kind="fan",Label="Calibrated radiator bank",
            RpmSensorId="rpm",VisuallyIdentified=true,VerifiedAt=now,
            Measurements=points
        };
        var normalized=FanCommissioningRules.Normalize([allPoints]).Single();
        Check(normalized.Measurements.Count==19 &&
              normalized.Measurements[0].DutyPercent==10 &&
              normalized.Measurements[^1].DutyPercent==100 &&
              normalized.VerifiedAt.HasValue,
            "save/settings normalization must preserve all 19 RPM calibration points");
        var stored=normalized.Measurements.Single(p=>p.DutyPercent==100);
        Check(stored.Temperatures.Count==1 &&
              stored.Temperatures[0].SensorId=="nvme" &&
              Math.Abs(stored.Temperatures[0].Celsius-47.5)<0.001,
            "normalization retains valid guard observations and drops invalid values");
        var roundTrip=System.Text.Json.JsonSerializer.Deserialize<FanChannelMapping>(
            System.Text.Json.JsonSerializer.Serialize(normalized))!;
        Check(roundTrip.Measurements.Count==19 &&
              roundTrip.Measurements.Single(p=>p.DutyPercent==100)
                  .Temperatures.Single().SensorName=="NVMe Temperature",
            "RPM/temperature calibration data must survive JSON persistence");

        // A fan must be discoverable and calibratable BEFORE any temperature
        // curve or live profile exists. Only the elevated, timed session may
        // touch that physical PWM output. Completion returns BIOS ownership.
        var preCurveDir=Directory.CreateTempSubdirectory("SensorDash-PreCurve-");
        try
        {
            var fanMap=new FanChannelMapping {
                ChannelId="pwm",Kind="fan",Label="Radiators",RpmSensorId="rpm"
            };
            var cfg=new DashboardSettings{FanChannelMappings=[fanMap]};
            WorkspaceLogic.Normalize(cfg);
            var outputs=new List<FanOutputInfo>{
                new("pwm","Radiator fan","SuperIO","superio",0,100,"Undefined",true)
            };
            var preCurve=new PersistentFanProfiles(preCurveDir.FullName);
            Check(!preCurve.HasActiveProfiles,
                "pre-curve calibration cannot require existing temperature curves");
            Check(CoolingEventRecorder.Accepted(preCurve.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    CalibrationMode:"identify",ConfirmExclusiveOwnership:true),cfg,outputs)),
                "unassigned physical fan can run high-duty identification");
            var fake=new FakePwm();
            var channels=new Dictionary<string,IFanPwmChannel>{{"pwm",fake}};
            var started=now.AddMinutes(1);
            preCurve.Tick(channels,Frame(started,10,490),started,cfg);
            Check(fake.SoftwareValue==100 && preCurve.HasCalibrationRunning,
                "pre-curve session commands high duty through the collector");
            for(var seconds=8;seconds<=20;seconds++)
                preCurve.Tick(channels,Frame(started.AddSeconds(seconds),100,1900),
                    started.AddSeconds(seconds),cfg);
            preCurve.Tick(channels,Frame(started.AddSeconds(21),100,1900),
                started.AddSeconds(21),cfg);
            Check(fake.SoftwareValue==85,
                "pre-curve identification progresses to next measured duty");
            for(var seconds=29;seconds<=41;seconds++)
                preCurve.Tick(channels,Frame(started.AddSeconds(seconds),85,1550),
                    started.AddSeconds(seconds),cfg);
            preCurve.Tick(channels,Frame(started.AddSeconds(42),85,1550),
                started.AddSeconds(42),cfg);
            Check(fake.SoftwareValue==70,
                "pre-curve identification reaches its lowest, conservative 70 percent step");
            for(var seconds=50;seconds<=62;seconds++)
                preCurve.Tick(channels,Frame(started.AddSeconds(seconds),70,1260),
                    started.AddSeconds(seconds),cfg);
            Check(!preCurve.HasCalibrationRunning &&
                  fake.IsSoftwareMode==false && fake.Resets>=2,
                "successful pre-curve identification must restore firmware control");
            var result=System.Text.Json.JsonSerializer.Serialize(
                preCurve.Snapshot(cfg));
            Check(result.Contains("\"mode\":\"identify\"") &&
                  result.Contains("\"state\":\"complete\""),
                "completed measured pairing remains available to the UI");
            Check(preCurve.DrainRecoveryEvents().Any(e=>e.Severity=="info"),
                "completed calibration emits a local history event");
            // Full low-duty sweep is blocked without a verified pairing and
            // explicit independent thermal monitors.
            Check(!CoolingEventRecorder.Accepted(preCurve.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    CalibrationMode:"response",TemperatureSensorIds:["temp"],
                    ConfirmExclusiveOwnership:true),cfg,outputs)),
                "an unverified tach pairing cannot authorize low-duty control");
            fanMap.VisuallyIdentified=true;
            fanMap.VerifiedAt=now;
            fanMap.Measurements=[
                new(){DutyPercent=70,Rpm=1260,ObservedAt=now},
                new(){DutyPercent=85,Rpm=1550,ObservedAt=now},
                new(){DutyPercent=100,Rpm=1900,ObservedAt=now}
            ];
            Check(CoolingEventRecorder.Accepted(preCurve.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    CalibrationMode:"response",TemperatureSensorIds:["temp"],
                    TemperatureLimit:85,ConfirmExclusiveOwnership:true),cfg,outputs)),
                "verified fan may calibrate full response before assigning a curve");
            var resumed=started.AddSeconds(75);
            preCurve.Tick(channels,Frame(resumed,70,1260,30),resumed,cfg);
            Check(fake.SoftwareValue==100,"full response begins at safe maximum");
            preCurve.Tick(channels,Frame(resumed.AddSeconds(1),100,1900,40),
                resumed.AddSeconds(1),cfg);
            Check(!preCurve.HasCalibrationRunning && !fake.IsSoftwareMode,
                "rising temperature must immediately abort full calibration and release hardware");
        }
        finally {try{preCurveDir.Delete(true);}catch{}}

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
                    ChannelIds=["pwm"],MinimumPercent=30,
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
            for(var second=8;second<=20;second++)
                profiles.Tick(channels,Frame(now.AddSeconds(second),100,1980),
                    now.AddSeconds(second),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(21),100,1980),
                now.AddSeconds(21),settings);
            // This test starts the default high-duty *identification*
            // phase; the separate pure response test above verifies all
            // 19 five-percent steps independently of temperature curves.
            Check(fake.SoftwareValue==85,
                "high-duty identification moves to 85 percent after stable 100 percent RPM");
            profiles.Tick(channels,Frame(now.AddSeconds(22),85,1650,80),
                now.AddSeconds(22),settings);
            Check(fake.SoftwareValue==85 && profiles.HasCalibrationRunning,
                "live temperature curve demanding 100% must NOT interrupt calibration at 85%");
            Check(CoolingEventRecorder.Accepted(profiles.RequestCalibration(
                new FanCommand("cancel-calibration",ChannelId:"pwm"),
                settings,outputs)),
                "manual cancellation must remain possible during calibration");
            profiles.Tick(channels,Frame(now.AddSeconds(23),85,1650,80),
                now.AddSeconds(23),settings);
            Check(fake.SoftwareValue>=99 && !profiles.HasCalibrationRunning,
                "manual cancellation restores the approved live curve");

            // Full physical calibration must start at 100%, descend to 10%
            // regardless of the enabled profile's 30% minimum or its 100%
            // temperature demand. Only selected thermal guards may abort.
            var verifiedMapping=settings.FanChannelMappings.Single(m=>m.ChannelId=="pwm");
            verifiedMapping.VerifiedAt=now;
            verifiedMapping.Measurements=[
                new(){DutyPercent=70,Rpm=1400,ObservedAt=now},
                new(){DutyPercent=85,Rpm=1700,ObservedAt=now},
                new(){DutyPercent=100,Rpm=1980,ObservedAt=now}
            ];
            Check(CoolingEventRecorder.Accepted(profiles.RequestCalibration(
                new FanCommand("calibrate",ChannelId:"pwm",
                    CalibrationMode:"response",TemperatureSensorIds:["temp"],
                    TemperatureLimit:85,ConfirmExclusiveOwnership:true),
                settings,outputs)),
                "verified fan full calibration must work while profile is active");
            var run=System.Text.Json.JsonSerializer.Serialize(profiles.Snapshot(settings));
            Check(run.Contains("\"totalSteps\":19"),
                "live profile's 30% minimum must not truncate physical 10-100% sweep");
            profiles.Tick(channels,Frame(now.AddSeconds(24),100,1980,80),
                now.AddSeconds(24),settings);
            for(var second=32;second<=44;second++)
                profiles.Tick(channels,Frame(now.AddSeconds(second),100,1980,80),
                    now.AddSeconds(second),settings);
            profiles.Tick(channels,Frame(now.AddSeconds(45),100,1980,80),
                now.AddSeconds(45),settings);
            Check(fake.SoftwareValue==95 && profiles.HasCalibrationRunning,
                "full calibration must descend despite live curve requesting 100%");
            profiles.Tick(channels,Frame(now.AddSeconds(46),95,1860,90),
                now.AddSeconds(46),settings);
            Check(fake.SoftwareValue>=99 && !profiles.HasCalibrationRunning,
                "selected thermal guard over limit must stop calibration and restore live curve");
            var snapshot=System.Text.Json.JsonSerializer.Serialize(profiles.Snapshot(settings));
            Check(snapshot.Contains("aborted") && snapshot.Contains("90.0"),
                "thermal-guard abort reason must be shown in read-only status");
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
