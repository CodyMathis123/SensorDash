// Advisory identification and measured-response data. None of these properties
// authorize hardware writes or alter persistent fan profile duty.
public sealed class FanCalibrationPoint
{
    public int DutyPercent { get; set; }
    public double Rpm { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
public sealed class FanChannelMapping
{
    public string ChannelId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Kind { get; set; } = "unknown"; // unknown, fan, pump
    public string RpmSensorId { get; set; } = "";
    public bool VisuallyIdentified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public List<FanCalibrationPoint> Measurements { get; set; } = [];
}

public static class FanCommissioningRules
{
    // A calibration is a measured high-duty response curve, NOT a lower
    // operating limit, a tachometer mapping guarantee, or a safe-to-run proof.
    public static bool HasVerifiedResponse(FanChannelMapping mapping)
    {
        if(mapping.Kind!="fan" || !mapping.VisuallyIdentified ||
           string.IsNullOrWhiteSpace(mapping.RpmSensorId))return false;
        var sorted=mapping.Measurements.OrderBy(p=>p.DutyPercent).ToArray();
        if(sorted.Length<2 || sorted[^1].DutyPercent-sorted[0].DutyPercent<10 ||
           sorted.Any(p => p.DutyPercent is <70 or >100 ||
                !double.IsFinite(p.Rpm) || p.Rpm<=0))return false;
        // Permit small tachometer noise; strongly decreasing intermediate
        // readings contradict the claimed response even if endpoints rise.
        for(var i=1;i<sorted.Length;i++)
            if(sorted[i].Rpm < sorted[i-1].Rpm*0.92)return false;
        return sorted[^1].Rpm>=sorted[0].Rpm*1.08;
    }

    public static List<FanChannelMapping> Normalize(IEnumerable<FanChannelMapping>? items)
    {
        return (items??[]).Where(m=>m!=null &&
            !string.IsNullOrWhiteSpace(m.ChannelId) && m.ChannelId.Length<=256)
            .Take(64).DistinctBy(m=>m.ChannelId,StringComparer.Ordinal)
            .Select(m=>{
                m.Label=(m.Label??"").Trim();
                if(m.Label.Length>80)m.Label=m.Label[..80];
                m.Kind=m.Kind is "fan" or "pump" ? m.Kind : "unknown";
                m.RpmSensorId=(!string.IsNullOrWhiteSpace(m.RpmSensorId) &&
                    m.RpmSensorId.Length<=256)?m.RpmSensorId:"";
                m.Measurements=(m.Kind=="fan" && m.RpmSensorId.Length>0
                    ?(m.Measurements??[]).Where(p=>p!=null &&
                        p.DutyPercent is >=70 and <=100 &&
                        double.IsFinite(p.Rpm) && p.Rpm>0 && p.Rpm<=100000 &&
                        p.ObservedAt>new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero) &&
                        p.ObservedAt<=DateTimeOffset.UtcNow.AddMinutes(5))
                        .OrderByDescending(p=>p.ObservedAt).DistinctBy(p=>p.DutyPercent)
                        .Take(8).OrderBy(p=>p.DutyPercent).ToList()
                    :[]);
                if(m.Kind!="fan" || !HasVerifiedResponse(m))
                    m.VerifiedAt=null;
                return m;
            }).ToList();
    }
}
