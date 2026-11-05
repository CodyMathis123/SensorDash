// Advisory identification and measured-response data. None of these properties
// authorize hardware writes or alter persistent fan profile duty.
// Historical thermal guards are diagnostic observations only. They do not
// change a fan's curve or authorize use of an unapproved PWM output.
public sealed class FanCalibrationTemperature
{
    public string SensorId { get; set; } = "";
    public string SensorName { get; set; } = "";
    public double Celsius { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
public sealed class FanCalibrationPoint
{
    public int DutyPercent { get; set; }
    public double Rpm { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    // Earlier saved measurements may have no temperature observations.
    public List<FanCalibrationTemperature> Temperatures { get; set; } = [];
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
    // A calibration is an observed duty/RPM response curve, NOT a guaranteed
    // safe operating floor or proof that every fan on a hub is spinning.
    public static bool HasVerifiedResponse(FanChannelMapping mapping)
    {
        if(mapping.Kind!="fan" || !mapping.VisuallyIdentified ||
           string.IsNullOrWhiteSpace(mapping.RpmSensorId))return false;
        var sorted=mapping.Measurements.OrderBy(p=>p.DutyPercent).ToArray();
        if(sorted.Length<2 || sorted[^1].DutyPercent-sorted[0].DutyPercent<10 ||
           sorted.Any(p => p.DutyPercent is <10 or >100 ||
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
                // Sanitize optional thermal observations without discarding
                // legacy fan calibrations that have no temperature metadata.
                foreach(var point in m.Measurements??[])
                {
                    if(point==null)continue;
                    point.Temperatures=(point.Temperatures??[])
                        .Where(t=>t!=null && !string.IsNullOrWhiteSpace(t.SensorId) &&
                            t.SensorId.Length<=256 && double.IsFinite(t.Celsius) &&
                            t.Celsius is >=-60 and <=150 &&
                            t.ObservedAt>new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero) &&
                            t.ObservedAt<=DateTimeOffset.UtcNow.AddMinutes(5))
                        .DistinctBy(t=>t.SensorId,StringComparer.Ordinal)
                        .Take(8).Select(t=>{
                            t.SensorName=(t.SensorName??"").Trim();
                            if(t.SensorName.Length>100)t.SensorName=t.SensorName[..100];
                            return t;
                        }).ToList();
                }
                m.Measurements=(m.Kind=="fan" && m.RpmSensorId.Length>0
                    ?(m.Measurements??[]).Where(p=>p!=null &&
                        p.DutyPercent is >=10 and <=100 &&
                        double.IsFinite(p.Rpm) && p.Rpm>0 && p.Rpm<=100000 &&
                        p.ObservedAt>new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero) &&
                        p.ObservedAt<=DateTimeOffset.UtcNow.AddMinutes(5))
                        .OrderByDescending(p=>p.ObservedAt).DistinctBy(p=>p.DutyPercent)
                        .Take(24).OrderBy(p=>p.DutyPercent).ToList()
                    :[]);
                if(m.Kind!="fan" || !HasVerifiedResponse(m))
                    m.VerifiedAt=null;
                return m;
            }).ToList();
    }
}
