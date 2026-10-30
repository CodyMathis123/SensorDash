// Persisted, administrator-approved snapshots are independent of editable UI drafts.
public sealed class ApprovedFanProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ChannelIds { get; set; } = [];
    public List<SimulatedFanCurveConfig> Curves { get; set; } = [];
    public double MinimumPercent { get; set; } = 20;
    public double HysteresisDegrees { get; set; } = 2;
    public double ResponseSeconds { get; set; } = 10;
}

public sealed record FanProfileReadout(string Id, string Name, string[] ChannelIds,
    string State, double? TargetPercent, string Message);
