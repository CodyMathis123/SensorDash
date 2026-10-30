// O(1) memory per sensor; raw SQLite measurements are never averaged into
// chart buckets before calculating true recorded peaks.
internal sealed record HistoricalSensorStats(
    string Id, string Type, int Count, double? Min, double? Max, long? PeakAt,
    double? Average, double? EnergyWh, double CoveredHours, double CoveragePercent);

internal sealed class HistoryStatsAccumulator(string id,string type,long start,long end,long maximumGapMs)
{
    private int _count;
    private double _sum,_min=double.PositiveInfinity,_max=double.NegativeInfinity,_wh,_coveredMs;
    private long? _peakAt,_previousTime;
    private double _previousWatts;
    internal void Add(long timestamp,double value)
    {
        if(timestamp<start||timestamp>end||!double.IsFinite(value))return;
        _count++;
        _sum+=value;
        if(value<_min)_min=value;
        if(value>_max){_max=value;_peakAt=timestamp;}
        if(type=="Power")
        {
            if(_previousTime is long prior)
            {
                var interval=timestamp-prior;
                // Ignore sleep, shutdown, missed collection and clock errors.
                if(interval>0 && interval<=maximumGapMs)
                {
                    _coveredMs+=interval;
                    _wh+=(Math.Max(0,value)+Math.Max(0,_previousWatts))*0.5
                         *interval/3600000.0;
                }
            }
            _previousTime=timestamp;
            _previousWatts=value;
        }
    }
    internal HistoricalSensorStats Finish()
    {
        var measuredHours=_coveredMs/3600000.0;
        var windowMs=Math.Max(1,end-start);
        return new HistoricalSensorStats(id,type,_count,
            _count==0?null:_min,_count==0?null:_max,_peakAt,
            _count==0?null:_sum/_count,
            type=="Power"&&_coveredMs>0?_wh:null,
            measuredHours,Math.Clamp(100*_coveredMs/windowMs,0,100));
    }
}
internal static class HistoryStatisticsSelfTest
{
    private static void Check(bool condition,string message)
    {
        if(!condition)throw new Exception("Overview history statistics: "+message);
    }
    internal static void Run()
    {
        const long hour=3600000;
        var temp=new HistoryStatsAccumulator("cpu","Temperature",0,8*hour,90000);
        temp.Add(0,38);temp.Add(10000,88);temp.Add(20000,42);
        Check(temp.Finish() is {Count:3,Max:88,PeakAt:10000},
            "A brief raw spike is not lost to chart downsampling");
        var p=new HistoryStatsAccumulator("gpu","Power",0,4*hour,90000);
        p.Add(0,100);p.Add(hour/60,100);
        p.Add(2*hour,250);p.Add(2*hour+hour/60,250);
        var result=p.Finish();
        Check(result.EnergyWh is double wh && Math.Abs(wh-5.83333333)<0.0001,
            "Do not integrate an overnight data gap (100W/min + 250W/min)");
        Check(result.CoveredHours>0 && result.CoveragePercent<1,
            "Coverage excludes the gap");
        Check(result.Max==250 && result.Average==175,
            "Raw peak and sample average are independent of energy coverage");
        var empty=new HistoryStatsAccumulator("power","Power",0,hour,90000);
        empty.Add(0,120);
        Check(empty.Finish() is {EnergyWh:null,Count:1},
            "A single power sample cannot imply any consumed energy");
        var sensor=new HistoryStatsAccumulator("temp","Temperature",0,hour,90000);
        sensor.Add(100,20);sensor.Add(200,30);
        Check(sensor.Finish().EnergyWh==null,
            "Temperature readings never claim electricity usage");
        Check(HistoryStore.StatsGapMs(10)==90000
            && HistoryStore.StatsGapMs(120)==360000,
            "Collection cadence determines bounded integration gap");
    }
}
