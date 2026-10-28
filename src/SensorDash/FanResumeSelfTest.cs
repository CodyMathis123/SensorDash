using System.Text.Json;

// No Windows power events or hardware needed: reproduce stale LHM software
// state across an artificial sleep gap, and verify bounded diagnostic output.
internal static class FanResumeSelfTest
{
    private sealed class FakePwm : IFanPwmChannel
    {
        public double Minimum=>0;
        public double Maximum=>100;
        public bool IsSoftwareMode {get;private set;}=true;
        public double SoftwareValue {get;private set;}=10;
        public int Writes {get;private set;}
        public int Resets {get;private set;}
        public void SetSoftware(double duty)
        {
            SoftwareValue=duty;IsSoftwareMode=true;Writes++;
        }
        public void SetDefault(){IsSoftwareMode=false;Resets++;}
    }

    private static void Check(bool yes,string reason)
    {
        if(!yes)throw new Exception("Fan sleep/resume regression: "+reason);
    }

    internal static void Run()
    {
        var now=new DateTimeOffset(2026,10,28,16,0,0,TimeSpan.Zero);
        Check(!FanResumePolicy.NeedsRearm(null,now,true),"First poll is not a resume");
        Check(!FanResumePolicy.NeedsRearm(now,now.AddSeconds(2),true),"Normal poll");
        Check(!FanResumePolicy.NeedsRearm(now,now.AddMinutes(3),false),
            "No rearm without an approved profile");
        Check(FanResumePolicy.NeedsRearm(now,now.AddSeconds(30),true),
            "Large gap implies resume or collection stall");
        Check(FanResumePolicy.NeedsRearm(now,now.AddSeconds(-5),true),
            "Clock going backwards requires rearm");

        var dir=Directory.CreateTempSubdirectory("SensorDash-Resume-");
        try
        {
            var trace=new FanControlTrace(dir.FullName);
            var settings=new DashboardSettings{
                SimulatedFanCurves=[new SimulatedFanCurveConfig{
                    Id="cpu",Name="CPU",SourceSensorId="temp",MaxSampleAgeSeconds=60,
                    Points=[new(){Temperature=30,Percent=10},
                            new(){Temperature=80,Percent=100}]
                }],
                FanProfiles=[new FanProfileConfig{
                    Id="radiator",Name="Radiator fans",ChannelIds=["fan1"],
                    CurveIds=["cpu"],MinimumPercent=10,HysteresisDegrees=3,
                    ResponseSeconds=10
                }],
                FanChannelMappings=[new FanChannelMapping{
                    ChannelId="fan1",Kind="fan",Label="Radiators",RpmSensorId="rpm1",
                    VisuallyIdentified=true
                }]
            };
            var profiles=new PersistentFanProfiles(dir.FullName,trace);
            var outputs=new List<FanOutputInfo>{
                new("fan1","PWM","Motherboard","hw",0,100,"Software",true)
            };
            var accepted=profiles.Request(new FanCommand("start-profile",ProfileId:"radiator",
                ConfirmExclusiveOwnership:true),settings,outputs,null);
            Check(JsonSerializer.Serialize(accepted).Contains("\"accepted\":true"),
                "Saved approval");
            var fake=new FakePwm();
            var channels=new Dictionary<string,IFanPwmChannel>{{"fan1",fake}};
            SensorFrame Frame(DateTimeOffset at)=>new(at,[
                new SensorReading("temp","CPU","CPU","CPU","Temperature",30,at),
                new SensorReading("rpm1","Radiator fan","Motherboard","SuperIO","Fan",490,at)
            ],null);
            profiles.Tick(channels,Frame(now),now,settings);
            Check(fake.Writes==1 && fake.Resets==1,"First takeover resets cached LHM mode");
            profiles.Tick(channels,Frame(now.AddSeconds(2)),now.AddSeconds(2),settings);
            Check(fake.Writes==1,"Unchanged PWM must not be rewritten every poll");
            profiles.Tick(channels,Frame(now.AddSeconds(65)),now.AddSeconds(65),settings);
            Check(fake.Writes==2 && fake.Resets==2,
                "Resume gap must force SetDefault then SetSoftware despite cached 10%");
            Check(trace.ResumeRearms==1,"One resume event");
            var lines=JsonSerializer.Serialize(trace.ReadRecent());
            Check(lines.Contains("REARM")&&lines.Contains("PWM"),
                "Text trace includes resume and sampled fan state");

            // A duplicate/other controller cannot receive a new authorization
            // merely because the resume policy was triggered.
            Check(!FanResumePolicy.NeedsRearm(now,now.AddSeconds(40),false),
                "Never rearm when no active approved profile");

            for(var i=0;i<100;i++)trace.Event("TEST","diagnostic "+i,now);
            Check(new FileInfo(trace.PathName).Length<2*1024*1024,
                "Low-volume log bounded");
        }
        finally
        {
            try { dir.Delete(true); } catch { }
        }
    }
}
