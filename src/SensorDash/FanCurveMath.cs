// The same pure curve interpolation is used by the read-only preview and live profiles.
// No hardware objects, clocks or authorization state are involved here.
internal static class FanCurveMath
{
    internal static double Interpolate(IReadOnlyList<CurvePointConfig> points, double temperature)
    {
        if (!double.IsFinite(temperature) || points.Count < 2)
            throw new ArgumentException("Curve needs at least two points and a finite temperature.");
        var ordered = points.OrderBy(p => p.Temperature).ToArray();
        if (temperature <= ordered[0].Temperature) return ordered[0].Percent;
        if (temperature >= ordered[^1].Temperature) return ordered[^1].Percent;
        for (var i = 1; i < ordered.Length; i++)
        {
            if (temperature > ordered[i].Temperature) continue;
            var a = ordered[i - 1];
            var b = ordered[i];
            if (b.Temperature <= a.Temperature)
                throw new ArgumentException("Curve temperatures must be strictly increasing.");
            return a.Percent + (b.Percent - a.Percent) *
                (temperature - a.Temperature) / (b.Temperature - a.Temperature);
        }
        throw new InvalidOperationException("No curve segment found.");
    }
}
