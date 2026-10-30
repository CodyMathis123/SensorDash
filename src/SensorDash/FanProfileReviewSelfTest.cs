internal static class FanProfileReviewSelfTest
{
    private static void Check(bool yes,string label)
    {
        if(!yes)throw new Exception("Fan profile review regression: "+label);
    }
    internal static void Run()
    {
        var approved=new ApprovedFanProfile{
            Id="rad",Name="Radiator",ChannelIds=["fanA"],MinimumPercent=10,
            HysteresisDegrees=3,ResponseSeconds=10,
            Curves=[new SimulatedFanCurveConfig{
                Id="cpu",Name="CPU",SourceSensorId="cpuTemp",MaxSampleAgeSeconds=30,
                Points=[new(){Temperature=40,Percent=10},new(){Temperature=55,Percent=10},
                    new(){Temperature=90,Percent=100}]
            }]
        };
        var cfg=new DashboardSettings{
            FanProfiles=[new FanProfileConfig{Id="rad",Name="Draft",ChannelIds=["fanA"],
                CurveIds=["cpu"],MinimumPercent=10,HysteresisDegrees=3,ResponseSeconds=10}],
            SimulatedFanCurves=[new SimulatedFanCurveConfig{
                Id="cpu",Name="Renamed CPU",SourceSensorId="cpuTemp",MaxSampleAgeSeconds=30,
                Points=[new(){Temperature=40,Percent=10},new(){Temperature=55,Percent=10},
                    new(){Temperature=90,Percent=100}]
            }]
        };
        Check(!FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Names and unchanged curves should not need reapproval");
        cfg.SimulatedFanCurves[0].Points[0].Percent=20;
        Check(FanProfileReview.Compare(approved,cfg).Reasons.Any(s=>s.Contains("curve edited")),
            "Editing selected curve must warn on pending approval");
        cfg.SimulatedFanCurves[0].Points[0].Percent=10;
        cfg.FanProfiles[0].MinimumPercent=20;
        Check(FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Minimum duty requires approval");
        cfg.FanProfiles[0].MinimumPercent=10;
        cfg.FanProfiles[0].ChannelIds=["fanB"];
        Check(FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Physical output reassignment needs approval");
        cfg.FanProfiles[0].ChannelIds=["fanA"];
        cfg.SimulatedFanCurves[0].SourceSensorId="gpuTemp";
        Check(FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Temperature sensor reassignment needs approval");
        cfg.SimulatedFanCurves.Clear();
        Check(FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Deleted curve requires approval");
        cfg.FanProfiles.Clear();
        Check(FanProfileReview.Compare(approved,cfg).NeedsReapproval,
            "Deleted draft cannot silently alter active collector snapshot");
    }
}
