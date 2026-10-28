using System.Text.Json;

// Full persistent-profile execution against fake PWM channels. Runs as part of
// --self-test in Windows CI: no hardware discovery, drivers, services or network.
internal static class FanControlSelfTest
{
    private sealed class FakePwm(double minimum = 0, double maximum = 100) : IFanPwmChannel
    {
        public double Minimum { get; set; } = minimum;
        public double Maximum { get; set; } = maximum;
        public bool IsSoftwareMode { get; private set; } = true; // LHM's cached previous mode.
        public double SoftwareValue { get; private set; } = 30;
        public bool FailWrites { get; set; }
        public bool FailRestore { get; set; }
        public int Writes { get; private set; }
        public int Resets { get; private set; }
        public void SetSoftware(double percent)
        {
            if (FailWrites) throw new IOException("Synthetic hardware write failed");
            if (!double.IsFinite(percent)) throw new Exception("Nonfinite PWM duty");
            SoftwareValue = percent;
            IsSoftwareMode = true;
            Writes++;
        }
        public void SetDefault()
        {
            Resets++;
            if (FailRestore) throw new IOException("Synthetic restore failed");
            IsSoftwareMode = false;
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception("Fan control regression: " + name);
    }

    private static void Close(double expected, double actual, string name, double tolerance = 0.15) =>
        Assert(Math.Abs(expected - actual) <= tolerance, name + ": got " + actual);

    private static SensorFrame Frame(DateTimeOffset time, double? cpu, double? gpu,
        DateTimeOffset? observed = null)
    {
        var values = new List<SensorReading>();
        if (cpu != null) values.Add(new SensorReading("cpu", "CPU", "Test", "Cpu",
            "Temperature", cpu, observed ?? time));
        if (gpu != null) values.Add(new SensorReading("gpu", "GPU", "Test", "Gpu",
            "Temperature", gpu, observed ?? time));
        return new SensorFrame(time, values, null);
    }

    private static SimulatedFanCurveConfig Curve(string id, string sensor,
        double low, double high) => new()
    {
        Id=id, Name=id, SourceSensorId=sensor, MaxSampleAgeSeconds=15,
        Points=[new(){Temperature=40,Percent=low},new(){Temperature=80,Percent=high}]
    };

    private static bool Accepted(object result) =>
        JsonSerializer.Serialize(result, JsonSerializerOptions.Web)
            .Contains("\"accepted\":true", StringComparison.Ordinal);

    public static void Run()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var settings = new DashboardSettings
        {
            SimulatedFanCurves=[Curve("cpu-curve","cpu",20,100),Curve("gpu-curve","gpu",10,90)],
            FanProfiles=[new FanProfileConfig
            {
                Id="shared",Name="Shared radiators",CurveIds=["cpu-curve","gpu-curve"],
                ChannelIds=["pwm1"],MinimumPercent=20,HysteresisDegrees=3,ResponseSeconds=10
            }]
        };
        WorkspaceLogic.Normalize(settings);
        var folder=Directory.CreateTempSubdirectory("SensorDash-PwmFake-");
        try
        {
        var profiles=new PersistentFanProfiles(folder.FullName);
        var known=new List<FanOutputInfo>{
            new("pwm1","Synthetic PWM","Motherboard","device",0,100,"Undefined",true)
        };
        Assert(!Accepted(profiles.Request(new FanCommand("start-profile",
            ProfileId:"shared",ConfirmExclusiveOwnership:false),settings,known,null)),
            "unapproved profile cannot be started");
        Assert(Accepted(profiles.Request(new FanCommand("start-profile",
            ProfileId:"shared",ConfirmExclusiveOwnership:true),settings,known,null)),
            "approved profile should start");
        Assert(new PersistentFanProfiles(folder.FullName).OwnsChannel("pwm1"),
            "approved snapshot must persist across restart");

        var fan=new FakePwm();
        var output=new Dictionary<string,IFanPwmChannel>{{"pwm1",fan}};
        // GPU 70C -> 70%; CPU 50C -> 40%. MAX mixing chooses GPU.
        profiles.Tick(output,Frame(now,50,70),now);
        Close(70,fan.SoftwareValue,"MAX curve chooses hottest demand");
        Assert(fan.Resets==1,"first takeover must clear stale LHM Software mode");
        Assert(fan.Writes==1,"first takeover must issue hardware write");

        profiles.Tick(output,Frame(now.AddSeconds(1),40,40),now.AddSeconds(1));
        Assert(fan.SoftwareValue>60 && fan.SoftwareValue<70,
            "falling demand should be time-smoothed, not instantly lowered");
        profiles.Tick(output,Frame(now.AddSeconds(2),80,40),now.AddSeconds(2));
        Close(100,fan.SoftwareValue,"rising CPU demand must apply immediately");

        // Missing either required input, bad values and stale samples are unsafe.
        profiles.Tick(output,Frame(now.AddSeconds(3),80,null),now.AddSeconds(3));
        Close(100,fan.SoftwareValue,"missing secondary source triggers maximum");
        Assert(JsonSerializer.Serialize(profiles.Snapshot()).Contains("failsafe"),
            "missing source should be reported as failsafe");
        profiles.Tick(output,Frame(now.AddSeconds(4),40,double.NaN),now.AddSeconds(4));
        Close(100,fan.SoftwareValue,"nonfinite temperature triggers maximum");
        profiles.Tick(output,Frame(now.AddSeconds(5),40,40,now.AddMinutes(-2)),now.AddSeconds(5));
        Close(100,fan.SoftwareValue,"stale temperature triggers maximum");
        profiles.Tick(output,Frame(now.AddSeconds(6),40,40,now.AddMinutes(2)),now.AddSeconds(6));
        Close(100,fan.SoftwareValue,"future timestamp triggers maximum");

        // Software exceptions are contained and default restoration is attempted.
        fan.FailWrites=true;
        profiles.Tick(output,Frame(now.AddSeconds(7),40,40),now.AddSeconds(7));
        Assert(JsonSerializer.Serialize(profiles.Snapshot()).Contains("degraded") && fan.Resets>=2,
            "hardware exception must enter degraded state and request firmware restore");
        fan.FailWrites=false;
        profiles.Tick(output,Frame(now.AddSeconds(8),40,40),now.AddSeconds(8));
        Assert(fan.Writes>1,"controller recovers after synthetic write fault");

        // Explicit stop relinquishes the channel on the next collector tick.
        var active=new PersistentFanProfiles(folder.FullName);
        Assert(Accepted(active.Request(new FanCommand("stop-profile",ProfileId:"shared"),
            settings,known,null)),"stop-profile should revoke authority");
        var stoppedFan=new FakePwm();
        var stoppedOutputs=new Dictionary<string,IFanPwmChannel>{{"pwm1",stoppedFan}};
        active.Tick(stoppedOutputs,Frame(now,40,40),now);
        Assert(stoppedFan.Resets==1 && stoppedFan.Writes==0,
            "stopping profile must request firmware/default without applying duty");
        Assert(!new PersistentFanProfiles(folder.FullName).HasActiveProfiles,
            "stopped snapshot must remain stopped after restart");

        // Absent channels cannot be restored now. Retry when discovery returns.
        var resumed=new PersistentFanProfiles(folder.FullName);
        Assert(Accepted(resumed.Request(new FanCommand("start-profile",
            ProfileId:"shared",ConfirmExclusiveOwnership:true),settings,known,null)),
            "profile should be reauthorizable after stop");
        Assert(Accepted(resumed.Request(new FanCommand("stop-all"),
            settings,known,null)),"stop-all should succeed");
        resumed.Tick(new Dictionary<string,IFanPwmChannel>(),Frame(now,40,40),now);
        var returningFan=new FakePwm();
        resumed.Tick(new Dictionary<string,IFanPwmChannel>{{"pwm1",returningFan}},
            Frame(now.AddSeconds(1),40,40),now.AddSeconds(1));
        Assert(returningFan.Resets==1,"missing release should retry after rediscovery");

        var invalid=new ApprovedFanProfile
        {
            Id="bad",ChannelIds=["pwm1"],Curves=[Curve("duplicate","cpu",20,50)]
        };
        invalid.Curves[0].Points[1].Temperature=40;
        Assert(!PersistentFanProfiles.Valid(invalid),"duplicate temperatures rejected");
        invalid.Curves[0].Points[1].Temperature=80;
        invalid.Curves[0].Points[1].Percent=double.NaN;
        Assert(!PersistentFanProfiles.Valid(invalid),"nonfinite duty rejected");

        // Test PWM bounds and failure recovery directly without any service.
        var writer=new FanPwmWriter();
        var limited=new FakePwm(35,85);
        Assert(writer.Apply("narrow",limited,new FanDutyDecision(100,true,"failsafe"),10,now)==null,
            "supported restricted PWM channel");
        Close(85,limited.SoftwareValue,"hardware maximum respected");
        var restoreBefore=limited.Resets;
        limited.Minimum=95;
        limited.Maximum=40;
        Assert(writer.Apply("narrow",limited,new FanDutyDecision(90,false,"test"),0,now.AddSeconds(1))!=null &&
            limited.Resets==restoreBefore+1 && !limited.IsSoftwareMode,
            "PWM capability loss restores firmware control instead of retaining unsafe duty");
        limited.Minimum=35;
        limited.Maximum=85;
        Assert(writer.Apply("narrow",limited,new FanDutyDecision(55,false,"test"),0,now.AddSeconds(2))==null,
            "recovered PWM capability should be usable again");
        var broken=new FakePwm {FailWrites=true};
        var failure=writer.Apply("broken",broken,new FanDutyDecision(55,false,"test"),10,now);
        Assert(failure!=null && broken.Resets>=1,"write error attempts default");
        var impossible=new FakePwm(95,40);
        Assert(writer.Apply("impossible",impossible,new FanDutyDecision(100,true,"test"),0,now)!=null,
            "invalid PWM bounds rejected");
        var beforeRelease=limited.Resets;
        writer.ReleaseAll(new Dictionary<string,IFanPwmChannel>{{"narrow",limited}});
        Assert(limited.Resets==beforeRelease+1 && !limited.IsSoftwareMode,
            "graceful writer shutdown must return firmware control");
        }
        finally
        {
            try { folder.Delete(true); } catch { /* cleanup best effort */ }
        }
    }
}
