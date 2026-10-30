using System.Text.Json;

// Electrical inventory is metadata. It cannot authorize PWM commands and it
// must survive settings persistence without accepting unbounded input.
internal static class CoolingElectricalSelfTest
{
    private static void Check(bool ok,string message)
    {
        if(!ok)throw new Exception("Cooling electrical model regression: "+message);
    }
    internal static void Run()
    {
        var cfg=new DashboardSettings
        {
            CoolingSystems=[
                new CoolingSystemConfig
                {
                    Id="main",Name="Main loop",Kind="liquid",
                    Components=[
                        new CoolingComponentConfig
                        {
                            Id="fans",Kind="fan-group",Name="Radiator fans",
                            SensorIds=["fan-rpm"],
                            Electrical=new CoolingElectricalConfig
                            {
                                Model="ARCTIC P12 Pro PST LN",
                                Quantity=19,Volts=12,AmpsPerUnit=0.11,
                                PowerSource="Powered PWM hub",
                                RpmSensorId="fan-rpm",ControlSensorId="fan-pwm",
                                MeasuredLowWatts=2.5,MeasuredHighWatts=24,
                                LowDutyPercent=10,HighDutyPercent=100
                            }
                        },
                        new CoolingComponentConfig
                        {
                            Id="pump",Kind="pump",Name="MCP355",
                            Electrical=new CoolingElectricalConfig
                            {
                                Model="Swiftech MCP355",Quantity=1,Volts=12,
                                WattsPerUnit=18,PowerSource="Molex"
                            }
                        }
                    ]
                }
            ]
        };
        WorkspaceLogic.Normalize(cfg);
        var fan=cfg.CoolingSystems[0].Components[0].Electrical!;
        Check(fan.Quantity==19 && fan.AmpsPerUnit==0.11
            && fan.ControlSensorId=="fan-pwm" && fan.LowDutyPercent==10,
            "Fan count, measurements and sensors round-trip");
        var json=JsonSerializer.Serialize(cfg,JsonSerializerOptions.Web);
        var loaded=JsonSerializer.Deserialize<DashboardSettings>(json,JsonSerializerOptions.Web)!;
        Check(loaded.CoolingSystems[0].Components[1].Electrical?.WattsPerUnit==18,
            "Fixed-speed pump nameplate retained");
        Check(loaded.CoolingSystems[0].Components[0].Electrical?.Quantity==19,
            "Quantity is persisted");
        var invalid=loaded.CoolingSystems[0].Components[0].Electrical!;
        invalid.Quantity=999;
        invalid.AmpsPerUnit=double.PositiveInfinity;
        invalid.Volts=-100;
        invalid.MeasuredLowWatts=100;
        invalid.MeasuredHighWatts=10;
        WorkspaceLogic.Normalize(loaded);
        Check(invalid.Quantity==100 && invalid.AmpsPerUnit==null && invalid.Volts==null,
            "Numeric specifications validated and bounded");
        Check(invalid.MeasuredLowWatts==null && invalid.HighDutyPercent==null,
            "Invalid calibration rejected, not presented as measured");
        Check(cfg.FanProfiles.Count==0 && cfg.FanChannelMappings.Count==0,
            "Electrical metadata does not invent writable fan outputs");
    }
}
