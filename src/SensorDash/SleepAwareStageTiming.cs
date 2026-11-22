using System.Diagnostics;
using System.Runtime.InteropServices;

// Stopwatch/QueryPerformanceCounter includes Windows sleep/hibernate time.
// The unbiased interrupt clock does not. Read both at each stage boundary;
// a large discrepancy identifies a stage interrupted by system suspension,
// rather than falsely classifying the entire suspended interval as device work.
// This is observational only and never changes PWM/control decisions.
internal readonly record struct StageDuration(
    double ElapsedMs, bool SleepInterrupted, double? ExcludedSleepMs)
{
    internal static StageDuration Classify(double stopwatchMs, double? awakeMs)
    {
        if (double.IsFinite(stopwatchMs) && stopwatchMs >= 0 &&
            awakeMs is double awake && double.IsFinite(awake) && awake >= 0)
        {
            // Ignore subsecond differences caused by sampling the two clocks
            // in sequence, timer jitter or CPU scheduling. A genuine blocking
            // hardware call while Windows is awake advances both clocks.
            var gap = Math.Max(0, stopwatchMs - awake);
            if (gap >= 2000 && gap >= stopwatchMs * .20)
                return new(stopwatchMs, true, gap);
        }
        return new(stopwatchMs, false, null);
    }
}

internal readonly record struct SleepAwareStageStamp(long QpcTicks, ulong? AwakeTicks100Ns)
{
    [DllImport("kernel32.dll", EntryPoint = "QueryUnbiasedInterruptTimePrecise")]
    private static extern void QueryUnbiasedInterruptTimePrecise(out ulong ticks100Ns);

    internal static SleepAwareStageStamp Start()
    {
        var started = Stopwatch.GetTimestamp();
        return new(started, ReadAwakeTicks());
    }

    internal StageDuration Finish()
    {
        var awakeEnd = ReadAwakeTicks();
        var completed = Stopwatch.GetTimestamp();
        var elapsedMs = Stopwatch.GetElapsedTime(QpcTicks, completed).TotalMilliseconds;
        double? awakeMs = AwakeTicks100Ns is ulong start && awakeEnd is ulong end &&
                          end >= start ? (end - start) / 10000.0 : null;
        return StageDuration.Classify(elapsedMs, awakeMs);
    }

    private static ulong? ReadAwakeTicks()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            QueryUnbiasedInterruptTimePrecise(out var ticks);
            return ticks;
        }
        catch (EntryPointNotFoundException) { return null; }
        catch (DllNotFoundException) { return null; }
    }
}
