// Hardware-free commissioning data validation and safety invariants.
internal static class FanCommissioningSelfTest
{
    private static void Check(bool value,string name)
    {
        if(!value)throw new Exception("Fan commissioning regression: "+name);
    }

    public static void Run()
    {
        var now=DateTimeOffset.UtcNow;
        var fan=new FanChannelMapping {
            ChannelId="/motherboard/control/1",Label="  Radiator A  ",Kind="fan",
            RpmSensorId="/motherboard/fan/1",VisuallyIdentified=true,
            VerifiedAt=now,
            Measurements=[
                new(){DutyPercent=70,Rpm=720,ObservedAt=now.AddMinutes(-3)},
                new(){DutyPercent=85,Rpm=1080,ObservedAt=now.AddMinutes(-2)},
                new(){DutyPercent=100,Rpm=1470,ObservedAt=now.AddMinutes(-1)}
            ]
        };
        Check(FanCommissioningRules.HasVerifiedResponse(fan),"three-point RPM response");
        var items=FanCommissioningRules.Normalize([fan,fan]);
        Check(items.Count==1 && items[0].Label=="Radiator A" &&
            items[0].VerifiedAt.HasValue,"normalization and deduplication");

        fan.Measurements.Add(new FanCalibrationPoint{DutyPercent=30,Rpm=100,ObservedAt=now});
        fan.Measurements.Add(new FanCalibrationPoint{DutyPercent=100,Rpm=double.NaN,ObservedAt=now});
        var cleaned=FanCommissioningRules.Normalize([fan])[0];
        Check(cleaned.Measurements.Count==3,"discard unsafe/invalid samples");

        var pump=new FanChannelMapping{
            ChannelId="pump",Kind="pump",RpmSensorId="rpm",
            VisuallyIdentified=true,VerifiedAt=now,
            Measurements=[new(){DutyPercent=70,Rpm=850,ObservedAt=now}]
        };
        var normalizedPump=FanCommissioningRules.Normalize([pump])[0];
        Check(normalizedPump.Measurements.Count==0 && normalizedPump.VerifiedAt==null,
            "pump cannot acquire fan calibration or verification");
        var runtime=new FanControlRuntime(Path.Combine(Path.GetTempPath(),
            "SensorDash-Commissioning-" + Guid.NewGuid().ToString("N")));
        var guard=System.Text.Json.JsonSerializer.Serialize(runtime.Request(
            new FanCommand("arm",ChannelId:"pump",Percent:100,DurationSeconds:30,
                ConfirmExclusiveOwnership:true),
            new DashboardSettings{FanChannelMappings=[normalizedPump]}));
        Check(guard.Contains("identified as a pump",StringComparison.Ordinal),
            "collector must reject manual tests for mapped pumps");
        var unknown=new FanChannelMapping{
            ChannelId="unknown",Kind="fan",RpmSensorId="rpm",
            VisuallyIdentified=true,VerifiedAt=now,
            Measurements=[
                new(){DutyPercent=70,Rpm=1010,ObservedAt=now},
                new(){DutyPercent=100,Rpm=1020,ObservedAt=now}
            ]
        };
        Check(!FanCommissioningRules.HasVerifiedResponse(unknown),
            "flat RPM response must not verify");
        Check(FanCommissioningRules.Normalize([unknown])[0].VerifiedAt==null,
            "failed verification must be cleared");

        var cfg=new DashboardSettings{FanChannelMappings=[fan]};
        WorkspaceLogic.Normalize(cfg);
        Check(cfg.FanChannelMappings.Count==1,"settings normalization retains mapping");
        Check(WorkspaceLogic.ReferencedSensorIds(cfg).Contains(fan.RpmSensorId),
            "referenced tachometer is included in in-use history");
    }
}
