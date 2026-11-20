using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A single background consumer owns fan-control.log creation and rotation.
// Hardware scanning, guard evaluation and PWM writes never wait for file I/O.
internal sealed class FanTraceWriterWorker(
    FanControlRuntime control, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _operations = loggerFactory.CreateLogger(OperationalEvents.Category);
    private long _lastReportedDropped;
    private long _writeErrors;
    private DateTimeOffset _lastNotice = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var trace = control.Trace;
        try
        {
            await foreach (var line in trace.PendingLines.ReadAllAsync(stoppingToken))
            {
                trace.MarkDequeued();
                Write(trace, line);
                NoticeIfNeeded(trace);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Make a bounded best-effort flush at normal service shutdown.
            // A crash cannot flush an in-memory diagnostic buffer.
            while (trace.PendingLines.TryRead(out var line))
            {
                trace.MarkDequeued();
                Write(trace, line);
            }
            NoticeIfNeeded(trace);
        }
    }

    private void Write(FanControlTrace trace, string line)
    {
        if (!trace.WriteBufferedLine(line)) _writeErrors++;
    }

    private void NoticeIfNeeded(FanControlTrace trace)
    {
        var dropped = trace.Dropped;
        if (dropped == _lastReportedDropped && _writeErrors == 0) return;
        var now = DateTimeOffset.UtcNow;
        if (now - _lastNotice < TimeSpan.FromMinutes(15)) return;
        if (dropped > _lastReportedDropped)
            _operations.LogWarning(OperationalEvents.FanTraceOverflow,
                "Fan diagnostic trace buffer dropped {Lost} lines ({Total} total); hardware/PWM control is unaffected.",
                dropped-_lastReportedDropped, dropped);
        if (_writeErrors > 0)
            _operations.LogWarning(OperationalEvents.FanTraceIoFailure,
                "Fan diagnostic file writer could not persist {Failures} lines; hardware/PWM control is unaffected.",
                _writeErrors);
        _lastNotice = now;
        _lastReportedDropped = dropped;
        _writeErrors = 0;
    }
}
