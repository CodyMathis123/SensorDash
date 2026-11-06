using System.IO.Pipes;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

public sealed record PipeCommand(string Op, string[]? Ids = null, double? Hours = null, DashboardSettings? Settings = null, TelemetryConfigurationUpdate? Telemetry = null);
public static class PipeProtocol
{
    public const string Name = "SensorDash.Telemetry.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
public static class CollectorClient
{
    public static async Task<JsonElement> AskAsync(PipeCommand request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            6));
        using var pipe = new NamedPipeClientStream(".", PipeProtocol.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, PipeProtocol.Json).AsMemory(), timeout.Token);
        var result = await reader.ReadLineAsync(timeout.Token);
        if (result == null) throw new IOException("Collector closed its local connection.");
        using var document = JsonDocument.Parse(result);
        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            document.RootElement.TryGetProperty("rpcError", out var error))
            throw new IOException(error.GetString());
        return document.RootElement.Clone();
    }
}
public sealed class CollectorPipeServer(DashboardState state, HistoryStore db, SimulationState simulation, FanControlRuntime liveFan, ILogger<CollectorPipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The collector accepts connections from local authenticated Windows users.
        // There are deliberately no fan-control WRITE commands. Read-only
        // inventory/status goes here; privileged writes use a separate pipe.
        // Settings changes are validated by DashboardState.UpdateSettings.
        var acl = new PipeSecurity();
        // Explicitly reject remote/network logons: only processes running locally may
        // talk to the service, even if Windows file sharing exposes named pipes.
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = NamedPipeServerStreamAcl.Create(PipeProtocol.Name, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 4096, 4096, acl);
                await stream.WaitForConnectionAsync(stoppingToken);
                var connected = stream;
                stream = null;
                _ = Task.Run(() => HandleAsync(connected, stoppingToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to accept sensor IPC connection");
                try { await Task.Delay(1000, stoppingToken); } catch (OperationCanceledException) { }
            }
            finally { stream?.Dispose(); }
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(50));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line == null || line.Length > 65536) throw new InvalidDataException("Invalid IPC request size.");
                var req = JsonSerializer.Deserialize<PipeCommand>(line, PipeProtocol.Json);
                if (req == null) throw new InvalidDataException("Invalid IPC request.");
                object response = req.Op switch
                {
                    "live" => state.Latest ?? new SensorFrame(DateTimeOffset.UtcNow, [], "Waiting for collector..."),
                    "settings" => state.Settings,
                    "telemetrySettings" => TelemetryConfiguration.Snapshot(),
                    "saveTelemetrySettings" when req.Telemetry != null => TelemetryConfiguration.Save(req.Telemetry),
                    "collectionGroups" => HardwareCollection.Groups,
                    "saveSettings" when req.Settings != null => Save(req.Settings),
                    "events" => db.ReadEvents(),
                    "simulation" => new { mode = "simulation-only", frequencyHz = 1, results = simulation.Latest },
                    "fanOutputs" => liveFan.Snapshot(state.Settings),
                    "fanDebug" => liveFan.DebugLog(),
                    "diagnostics" => Diagnostics(),
                    "performanceHistory" => ProcessTelemetry.History(),
                    "status" => new
                    {
                        app = "SensorDash", version = typeof(DashboardState).Assembly.GetName().Version?.ToString(3) ?? "unknown", mode = "service",
                        started = state.Started, now = DateTimeOffset.UtcNow,
                        sensorCount = state.Latest?.Sensors.Count ?? 0,
                        lastSample = state.Latest?.Timestamp, error = state.Latest?.Error,
                        storagePollSeconds = state.Settings.StoragePollSeconds
                    },
                    _ => new { rpcError = "Unknown or unsupported collector command." }
                };
                var serializationStarted = Stopwatch.GetTimestamp();
                var serialized = JsonSerializer.Serialize(response, PipeProtocol.Json);
                CollectorPerformance.ObserveIpcResponse(Encoding.UTF8.GetByteCount(serialized),
                    Stopwatch.GetElapsedTime(serializationStarted).TotalMilliseconds);
                await writer.WriteLineAsync(serialized.AsMemory(), deadline.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Rejected or failed sensor IPC request");
            }
        }
    }
    private DashboardSettings Save(DashboardSettings settings)
    {
        state.UpdateSettings(settings);
        return state.Settings;
    }
    private object Diagnostics()
    {
        var snapshot = state.Latest;
        var storage = snapshot?.Devices?.Where(d => d.Type == "Storage").ToArray() ?? [];
        var readings = snapshot?.Sensors.Where(s => s.DeviceType == "Storage").ToArray() ?? [];
        return new
        {
            pawnIo = snapshot?.PawnIo,
            isAdministrator = SecurityInfo.IsAdministrator(),
            storageEnabled = true,
            storagePollSeconds = state.Settings.StoragePollSeconds,
            storageDevices = storage,
            storageSensorCount = readings.Length,
            process = ProcessTelemetry.Snapshot(),
            storageTemperatures = readings.Where(s => s.Type == "Temperature").ToArray(),
            storageLastPolled = state.LastStoragePoll,
            note = storage.Length == 0
                ? "The elevated collector did not discover storage devices. Check the Windows service event log."
                : readings.All(s => s.Type != "Temperature")
                    ? "Storage devices were found, but the library exposed no temperature sensor."
                    : "Storage readings are available. Assign the desired SSD sensor in Settings."
        };
    }
}
