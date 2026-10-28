using System.Globalization;
using System.Text;

// Low-volume, bounded, human-readable collector-side diagnostics. Deliberately
// independent of SQLite telemetry, fan controllers, and logger providers.
// Exceptions never affect PWM writes. No sensor data is retained in memory.
internal sealed class FanControlTrace
{
    private const long MaxFileBytes=2*1024*1024;
    private readonly string _path;
    private readonly object _sync=new();
    private readonly Dictionary<string,(DateTimeOffset Time,string State,double Duty)>
        _samples=new(StringComparer.Ordinal);
    private int _resumeRearms;

    internal string PathName => _path;
    internal int ResumeRearms => Volatile.Read(ref _resumeRearms);

    internal FanControlTrace(string dataDirectory)
    {
        _path=Path.Combine(dataDirectory,"logs","fan-control.log");
    }

    internal void ResumeGap(TimeSpan gap, IEnumerable<string> channels,DateTimeOffset now)
    {
        Interlocked.Increment(ref _resumeRearms);
        Event("REARM", "Collector polling gap "+gap.TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+
            "s (possible sleep/resume); forcing fresh PWM takeover on approved channels: "+
            string.Join(",",channels),now);
    }

    internal object ReadRecent(int maxLines=250)
    {
        try
        {
            // Read only on explicit diagnostic request. The file is bounded
            // to 2 MiB and its rotated predecessor is retained separately.
            lock(_sync)
            {
                var lines=File.Exists(_path)
                    ?File.ReadLines(_path).TakeLast(Math.Clamp(maxLines,10,500)).ToArray()
                    :Array.Empty<string>();
                return new {path=_path,rotatedPath=_path+".1",lines};
            }
        }
        catch(Exception ex)
        {
            return new {path=_path,rotatedPath=_path+".1",
                lines=new[]{"Log temporarily unavailable ("+ex.GetType().Name+")."}};
        }
    }

    internal void Event(string kind,string detail,DateTimeOffset? time=null)
    {
        var clean=detail.Replace('\r',' ').Replace('\n',' ');
        if(clean.Length>950)clean=clean[..950];
        var line=(time??DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("O",CultureInfo.InvariantCulture)+
            " ["+kind+"] "+clean+Environment.NewLine;
        try
        {
            lock(_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if(File.Exists(_path) && new FileInfo(_path).Length+Encoding.UTF8.GetByteCount(line)>MaxFileBytes)
                {
                    var previous=_path+".1";
                    File.Move(_path,previous,true);
                }
                File.AppendAllText(_path,line,Encoding.UTF8);
            }
        }
        catch { /* Logging must never interrupt collector or fan commands. */ }
    }

    internal void Observe(string profileId,string channelId,string state,
        double requestedDuty,double? writtenDuty,bool softwareMode,
        string? mappedSensor,double? rpm,DateTimeOffset now)
    {
        bool emit;
        lock(_sync)
        {
            var key=profileId+"|"+channelId;
            var previous=_samples.GetValueOrDefault(key);
            emit=previous.Time==default || now-previous.Time>=TimeSpan.FromSeconds(60)
                || previous.State!=state
                || (now-previous.Time>=TimeSpan.FromSeconds(5)
                    && Math.Abs(previous.Duty-requestedDuty)>=10);
            if(emit)_samples[key]=(now,state,requestedDuty);
        }
        if(!emit)return;
        var format=(double? x)=>x is double value && double.IsFinite(value)
            ?value.ToString("0.0",CultureInfo.InvariantCulture):"n/a";
        Event("PWM", "profile="+profileId+" channel="+channelId+
            " state="+state+" requested="+format(requestedDuty)+
            "% lastCommand="+format(writtenDuty)+
            "% lhmSoftwareMode="+softwareMode+
            " rpmSensor="+(mappedSensor??"none")+
            " rpm="+format(rpm)+
            " (mode/value are LHM's reported state, not confirmed physical PWM)",now);
    }
}
