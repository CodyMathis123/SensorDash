using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

// JSON serialization and per-browser fanout are observers, not part of the
// hardware acquisition/control transaction. DashboardState.Latest is updated
// synchronously; this worker may coalesce superseded UI frames when slow.
internal sealed class LiveFrameBroadcastWorker(
    DashboardState state, ILogger<LiveFrameBroadcastWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var frame in state.PendingLiveFrames.ReadAllAsync(stoppingToken))
            {
                try
                {
                    state.BroadcastSerialized(JsonSerializer.Serialize(frame, JsonSerializerOptions.Web));
                }
                catch (Exception error)
                {
                    logger.LogWarning(error,
                        "Live sensor UI serialization failed; hardware monitoring and fan control are unaffected.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
