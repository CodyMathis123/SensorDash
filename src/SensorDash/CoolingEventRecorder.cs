using System.Text.Json;

// Records successful privileged fan *requests* for Cooling Systems' history
// context. It does not assert that a pending collector write physically occurred.
internal sealed record CoolingAuditEntry(string Sensor, string Message);
internal static class CoolingEventRecorder
{
    internal static bool Accepted(object result)
    {
        var json=JsonSerializer.SerializeToElement(result,PipeProtocol.Json);
        return json.TryGetProperty("accepted",out var flag) &&
               flag.ValueKind==JsonValueKind.True;
    }

    internal static CoolingAuditEntry? Describe(FanCommand? command)
    {
        if(command==null)return null;
        return command.Action switch
        {
            "arm" => new("fan:channel:"+command.ChannelId,
                command.CurveId.Length>0
                    ? "Timed fan curve test authorized; collector application pending."
                    : $"Manual PWM test authorized at {command.Percent:0.#}% for {command.DurationSeconds}s; collector application pending."),
            "release" => new("fan:all","Manual fan control release requested; firmware restoration is best-effort."),
            "start-profile" => new("fan:profile:"+command.ProfileId,
                "Fan profile approval accepted; collector application pending."),
            "stop-profile" => new("fan:profile:"+command.ProfileId,
                "Fan profile stop requested; firmware restoration is best-effort."),
            "stop-all" => new("fan:all",
                "All fan profiles stopped by request; firmware restoration is best-effort."),
            _ => null
        };
    }
}

internal static class CoolingEventRecorderSelfTest
{
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception("Cooling event regression: "+message);
    }
    internal static void Run()
    {
        Check(!CoolingEventRecorder.Accepted(new {accepted=false}),"Rejected command not logged");
        Check(CoolingEventRecorder.Accepted(new {accepted=true}),"Accepted request recognized");
        Check(CoolingEventRecorder.Describe(new FanCommand("arm",ChannelId:"pwm1",
            Percent:85,DurationSeconds:30)) is {Sensor:"fan:channel:pwm1"},
            "Manual test target recorded");
        Check(CoolingEventRecorder.Describe(new FanCommand("start-profile",ProfileId:"radiators"))
            is {Sensor:"fan:profile:radiators"},"Profile approval target recorded");
        Check(CoolingEventRecorder.Describe(new FanCommand("stop-all"))?.Sensor=="fan:all",
            "Global stop is visible without inventing a channel");
        Check(CoolingEventRecorder.Describe(new FanCommand("bogus"))==null,
            "Unknown request ignored");
    }
}
