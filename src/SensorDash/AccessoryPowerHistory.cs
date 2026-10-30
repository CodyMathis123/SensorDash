using Microsoft.Data.Sqlite;

internal sealed record AccessoryPowerHistory(string Name, string System, string Source,
    double? RatedWatts, HistoricalSensorStats Stats);

internal static class AccessoryPowerModel
{
    // Match the browser's per-unit curve interpolation. Incomplete rows are drafts.
    internal static double? Curve(CoolingElectricalConfig e, double? duty, double? rpm)
    {
        double? At(double? value, bool byRpm)
        {
            if (value is not double x || !double.IsFinite(x) || x < 0 || x > (byRpm ? 100000 : 100)) return null;
            var points = (e.PowerCurve ?? []).Where(p=>p is not null)
                .Select(p=>(X: byRpm ? p.Rpm : p.DutyPercent, W: p.Watts))
                .Where(p=>p.X is >= 0 && p.X <= (byRpm ? 100000 : 100) && p.W is >= 0 and <= 1000)
                .OrderBy(p=>p.X).ToArray();
            if (points.Length < 2 || points.Select(p=>p.X).Distinct().Count()!=points.Length) return null;
            // Clamp outside the measured range; never extrapolate negative power.
            if (x <= points[0].X) return points[0].W;
            for (int i=1;i<points.Length;i++)
                if (x <= points[i].X)
                {
                    var a=points[i-1]; var b=points[i];
                    return a.W + (b.W-a.W)*(x-a.X)/(b.X-a.X);
                }
            return points[^1].W;
        }
        if (e.Quantity is < 1 or > 100) return null;
        return (At(rpm,true) ?? At(duty,false)) * e.Quantity;
    }

    internal static double? Rating(CoolingElectricalConfig e) => e.WattsPerUnit is double w
        ? w * e.Quantity : e.Volts is double v && e.AmpsPerUnit is double a ? v * a * e.Quantity : null;

    internal static (double Watts, string Source)? Estimate(CoolingElectricalConfig e,
        double? measured, double? duty, double? rpm)
    {
        if (measured is >= 0 && double.IsFinite(measured.Value)) return (measured.Value, "Measured");
        if (duty is >= 0 and <= 100 && e.LowDutyPercent is double lo && e.HighDutyPercent is double hi &&
            hi > lo && e.MeasuredLowWatts is double low && low >= 0 && e.MeasuredHighWatts is double high && high >= low)
        {
            var watts = low + (high - low)
                * Math.Clamp((duty.Value - lo) / (hi - lo), 0, 1);
            return (watts, "Calibrated estimate");
        }
        if (Curve(e,duty,rpm) is double curve) return (curve, "Power curve estimate");
        // RPM establishes observed runtime; it is not a power scaling factor.
        // Zero PWM cannot establish that a fan stopped (many retain a minimum).
        if (rpm is >= 0 && Rating(e) is double rated) return (rpm > 0 ? rated : 0, "Rated estimate");
        return null;
    }
}

public sealed partial class HistoryStore
{
    internal List<AccessoryPowerHistory> ReadAccessoryStatistics(DashboardSettings settings,
        IReadOnlyDictionary<string, string> types, double hours)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from = now - (long)(Math.Clamp(hours, 0.1, 26280) * 3600000);
        var results = new List<AccessoryPowerHistory>();
        using var connection = Open();
        foreach (var system in settings.CoolingSystems)
        foreach (var component in system.Components.Where(c => c.Kind is "fan-group" or "pump"))
        {
            var e = component.Electrical ?? new CoolingElectricalConfig();
            var rpmId = !string.IsNullOrEmpty(e.RpmSensorId) ? e.RpmSensorId :
                component.SensorIds.FirstOrDefault(id => types.GetValueOrDefault(id) == "Fan") ?? "";
            var stats = new HistoryStatsAccumulator(component.Id, "Power", from, now, StatsGapMs(settings.SaveEverySeconds));
            var sources = new HashSet<string>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ts,
                    MAX(CASE WHEN sensor=$power THEN value END),
                    MAX(CASE WHEN sensor=$duty THEN value END),
                    MAX(CASE WHEN sensor=$rpm THEN value END)
                FROM samples WHERE sensor IN ($power,$duty,$rpm) AND ts >= $from AND ts <= $to
                GROUP BY ts ORDER BY ts
                """;
            command.Parameters.AddWithValue("$power", e.PowerSensorId);
            command.Parameters.AddWithValue("$duty", e.ControlSensorId);
            command.Parameters.AddWithValue("$rpm", rpmId);
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", now);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    double? Value(int column) => reader.IsDBNull(column) ? null : reader.GetDouble(column);
                    if (AccessoryPowerModel.Estimate(e, Value(1), Value(2), Value(3)) is { } estimate)
                    {
                        stats.Add(reader.GetInt64(0), estimate.Watts);
                        sources.Add(estimate.Source);
                    }
                }
            results.Add(new(component.Name, system.Name,
                sources.Count == 1 ? sources.Single() : sources.Count > 1 ? "Mixed estimate" : AccessoryPowerModel.Rating(e) is not null ? "Rating only" : "Unavailable",
                AccessoryPowerModel.Rating(e), stats.Finish()));
        }
        return results;
    }
}
