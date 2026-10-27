using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

// Workspace definitions contain only logical references to discovered sensors.
// No physical sensor IDs, specific chipsets, or cooling hardware are hardcoded.
public sealed class CoolingSystemConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New cooling system";
    public string Kind { get; set; } = "custom"; // liquid, air, custom
    public List<CoolingComponentConfig> Components { get; set; } = [];
}
public sealed class CoolingComponentConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Component";
    public string Kind { get; set; } = "sensor"; // heat-source, pump, radiator, fan, sensor, other
    public List<string> SensorIds { get; set; } = [];
}
public sealed class WorkspaceDashboardConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New dashboard";
    public bool Locked { get; set; } = false; // UI editing lock, not an authorization barrier
    public List<WorkspaceWidgetConfig> Widgets { get; set; } = [];
}
public sealed class WorkspaceWidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Sensor";
    public string Kind { get; set; } = "metric"; // metric, chart, readings
    public List<string> SensorIds { get; set; } = [];
}
public sealed class TrayWidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SensorId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#62d5ce";
    public int Decimals { get; set; } = 0;
}
public sealed class VirtualSensorConfig
{
    public string Id { get; set; } = "virtual:" + Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Virtual sensor";
    public string Operation { get; set; } = "max"; // max, min, average, delta
    public List<string> SensorIds { get; set; } = [];
}
public sealed class CurvePointConfig
{
    public double Temperature { get; set; }
    public double Percent { get; set; }
}
public sealed class SimulatedFanCurveConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New fan curve";
    public string SourceSensorId { get; set; } = "";
    public string OutputSensorId { get; set; } = ""; // optional: display correlation ONLY, never write
    public int MaxSampleAgeSeconds { get; set; } = 30;
    public List<CurvePointConfig> Points { get; set; } = [
        new() { Temperature = 30, Percent = 25 },
        new() { Temperature = 50, Percent = 55 },
        new() { Temperature = 75, Percent = 100 }
    ];
}
public sealed record SimulatedFanCurveReading(string Id, string Name, string SourceSensorId,
    string OutputSensorId, double? InputTemperature, double? TargetPercent,
    string State, DateTimeOffset EvaluatedAt);

public sealed class SimulationState
{
    private IReadOnlyList<SimulatedFanCurveReading> _latest = [];
    public IReadOnlyList<SimulatedFanCurveReading> Latest => Volatile.Read(ref _latest);
    public void Update(IReadOnlyList<SimulatedFanCurveReading> values) => Volatile.Write(ref _latest, values);
}

public static class WorkspaceLogic
{
    private static string Short(string? value, int max = 100) =>
        (value ?? "").Trim() is string s && s.Length > max ? s[..max] : (value ?? "").Trim();
    private static List<string> Ids(List<string>? ids, int limit = 16) =>
        (ids ?? []).Where(s => !string.IsNullOrWhiteSpace(s) && s.Length <= 256).Distinct().Take(limit).ToList();

    public static HashSet<string> ReferencedSensorIds(DashboardSettings cfg)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Include(IEnumerable<string>? ids)
        {
            if(ids == null)return;
            foreach(var id in ids)if(!string.IsNullOrWhiteSpace(id))used.Add(id);
        }
        Include(cfg.PinnedSensors);
        Include(cfg.HighThresholds?.Keys);
        Include(cfg.SensorBindings?.Values);
        foreach(var dashboard in cfg.Dashboards ?? [])
            foreach(var widget in dashboard.Widgets ?? [])Include(widget.SensorIds);
        foreach(var system in cfg.CoolingSystems ?? [])
            foreach(var part in system.Components ?? [])Include(part.SensorIds);
        foreach(var tray in cfg.TrayWidgets ?? [])Include([tray.SensorId]);
        foreach(var derived in cfg.VirtualSensors ?? [])
        {
            Include([derived.Id]);
            Include(derived.SensorIds);
        }
        foreach(var curve in cfg.SimulatedFanCurves ?? [])
            Include([curve.SourceSensorId, curve.OutputSensorId]);
        return used;
    }

    public static void Normalize(DashboardSettings cfg)
    {
        // Hiding a sensor is presentation-only. It never removes the sensor from
        // collector polling or breaks configured alert/control simulations.
        cfg.HiddenSensorIds = Ids(cfg.HiddenSensorIds, 2000);
        cfg.HiddenDeviceIds = Ids(cfg.HiddenDeviceIds, 250);
        cfg.HiddenHardwareTypes = (cfg.HiddenHardwareTypes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        cfg.HistoryMode = cfg.HistoryMode is "balanced" or "in-use" or "all"
            ? cfg.HistoryMode : "balanced";
        cfg.ExcludedHistoryHardwareTypes = (cfg.ExcludedHistoryHardwareTypes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 80)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
        cfg.CoolingSystems = (cfg.CoolingSystems ?? []).Take(20).Select(system => {
            system.Id = Short(system.Id, 80);
            system.Name = Short(system.Name);
            system.Kind = system.Kind is "liquid" or "air" ? system.Kind : "custom";
            system.Components = (system.Components ?? []).Take(32).Select(component => {
                component.Id = Short(component.Id, 80);
                component.Name = Short(component.Name);
                component.Kind = component.Kind is "heat-source" or "pump" or "radiator" or "fan" or "sensor" or "other" ? component.Kind : "other";
                component.SensorIds = Ids(component.SensorIds);
                return component;
            }).ToList();
            return system;
        }).Where(c => c.Id.Length > 0 && c.Name.Length > 0).DistinctBy(c=>c.Id).ToList();

        cfg.Dashboards = (cfg.Dashboards ?? []).Take(20).Select(dashboard => {
            dashboard.Id = Short(dashboard.Id, 80);
            dashboard.Name = Short(dashboard.Name);
            dashboard.Widgets = (dashboard.Widgets ?? []).Take(30).Select(widget => {
                widget.Id = Short(widget.Id, 80);
                widget.Title = Short(widget.Title);
                widget.Kind = widget.Kind is "metric" or "chart" or "readings" ? widget.Kind : "metric";
                widget.SensorIds = Ids(widget.SensorIds, 12);
                return widget;
            }).Where(w => w.Id.Length > 0).DistinctBy(w=>w.Id).ToList();
            return dashboard;
        }).Where(d => d.Id.Length > 0 && d.Name.Length > 0).DistinctBy(d=>d.Id).ToList();

        cfg.TrayWidgets = (cfg.TrayWidgets ?? []).Where(w=>!string.IsNullOrWhiteSpace(w.SensorId))
            .Take(6).Select(w=>{
                w.Id=Short(w.Id,80);
                w.SensorId=Short(w.SensorId,256);
                w.Label=Short(w.Label,40);
                if (!System.Text.RegularExpressions.Regex.IsMatch(w.Color ?? "", "^#[0-9a-fA-F]{6}$")) w.Color="#62d5ce";
                w.Decimals=Math.Clamp(w.Decimals,0,1);
                return w;
            }).Where(w=>w.Id.Length>0 && w.SensorId.Length>0).DistinctBy(w=>w.Id).ToList();

        cfg.VirtualSensors = (cfg.VirtualSensors ?? []).Take(32).Select(v=>{
            v.Id=Short(v.Id,80);
            if(!v.Id.StartsWith("virtual:",StringComparison.Ordinal))v.Id="virtual:"+v.Id;
            v.Name=Short(v.Name);
            v.Operation=v.Operation is "max" or "min" or "average" or "delta" ? v.Operation : "max";
            v.SensorIds=Ids(v.SensorIds,8);
            return v;
        }).Where(v=>v.Id.Length>8 && v.Name.Length>0).DistinctBy(v=>v.Id).ToList();

        cfg.SimulatedFanCurves = (cfg.SimulatedFanCurves ?? []).Take(24).Select(curve=>{
            curve.Id=Short(curve.Id,80);
            curve.Name=Short(curve.Name);
            curve.SourceSensorId=Short(curve.SourceSensorId,256);
            curve.OutputSensorId=Short(curve.OutputSensorId,256);
            curve.MaxSampleAgeSeconds=Math.Clamp(curve.MaxSampleAgeSeconds,3,300);
            curve.Points=(curve.Points ?? []).Where(p=>double.IsFinite(p.Temperature)&&double.IsFinite(p.Percent))
                .Select(p=>new CurvePointConfig{Temperature=Math.Clamp(p.Temperature,-30,150),
                    Percent=Math.Clamp(p.Percent,0,100)})
                .OrderBy(p=>p.Temperature).DistinctBy(p=>p.Temperature).Take(16).ToList();
            return curve;
        }).Where(c=>c.Id.Length>0&&c.Name.Length>0 &&c.Points.Count>=2).DistinctBy(c=>c.Id).ToList();
    }

    public static List<SensorReading> VirtualReadings(SensorFrame frame, List<VirtualSensorConfig> definitions)
    {
        var catalog = frame.Sensors.ToDictionary(s=>s.Id,s=>s);
        var output = new List<SensorReading>();
        // One-pass calculation intentionally disallows virtual-to-virtual references.
        // That keeps cycles and ordering out of this initial version.
        foreach(var def in definitions)
        {
            var values = def.SensorIds
                .Where(id => !id.StartsWith("virtual:",StringComparison.Ordinal))
                .Select(id => catalog.GetValueOrDefault(id))
                .Where(s => s?.Value is double v && double.IsFinite(v))
                .ToArray();
            if(values.Length==0 || (def.Operation=="delta"&&values.Length<2))continue;
            var numeric=values.Select(s=>s!.Value!.Value).ToArray();
            double result=def.Operation switch {
                "min"=>numeric.Min(), "average"=>numeric.Average(),
                "delta"=>numeric[0]-numeric[1], _=>numeric.Max()
            };
            var unitType=values[0]!.Type;
            if(values.Any(s=>s!.Type!=unitType))continue;
            output.Add(new SensorReading(def.Id, def.Name,"Virtual sensors","Virtual",unitType,result,
                values.Min(s=>s!.ObservedAt) ?? frame.Timestamp));
        }
        return output;
    }

    public static List<SimulatedFanCurveReading> Evaluate(
        SensorFrame? frame, DashboardSettings cfg, DateTimeOffset now)
    {
        var lookup=frame?.Sensors.ToDictionary(s=>s.Id,s=>s) ?? new Dictionary<string,SensorReading>();
        return cfg.SimulatedFanCurves.Select(curve=>{
            if(!lookup.TryGetValue(curve.SourceSensorId,out var input) ||
                input.Type!="Temperature" || input.Value is not double value || !double.IsFinite(value))
                return new SimulatedFanCurveReading(curve.Id,curve.Name,curve.SourceSensorId,curve.OutputSensorId,
                    null,null,"missing-input",now);
            var observed = input.ObservedAt ?? frame!.Timestamp;
            if ((now-observed).TotalSeconds>curve.MaxSampleAgeSeconds || (now-observed).TotalSeconds< -5)
                return new SimulatedFanCurveReading(curve.Id,curve.Name,curve.SourceSensorId,curve.OutputSensorId,
                    value,null,"stale-input",now);
            var points=curve.Points;
            double result=points[0].Percent;
            // Outside the configured temperature range, hold the nearest
            // endpoint; never extrapolate through the first line segment.
            if(value<=points[0].Temperature) result=points[0].Percent;
            else if(value>=points[^1].Temperature) result=points[^1].Percent;
            else {
                for(var i=1;i<points.Count;i++){
                    if(value>points[i].Temperature)continue;
                    var prev=points[i-1];var next=points[i];
                    result=prev.Percent+(next.Percent-prev.Percent)*(value-prev.Temperature)/(next.Temperature-prev.Temperature);
                    break;
                }
            }
            return new SimulatedFanCurveReading(curve.Id,curve.Name,curve.SourceSensorId,curve.OutputSensorId,
                value,Math.Clamp(result,0,100),"simulated",now);
        }).ToList();
    }
}

// This is an independent 1 Hz calculator. It has no reference to IControl,
// hardware registers, fan-setting interfaces, or output-writing commands.
public sealed class SimulationWorker(DashboardState state, SimulationState results) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do {
            results.Update(WorkspaceLogic.Evaluate(state.Latest,state.Settings,DateTimeOffset.UtcNow));
        } while(await tick.WaitForNextTickAsync(stoppingToken));
    }
}
