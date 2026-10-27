using System.Windows.Forms;
using System.Drawing;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;

public sealed record PipeCommand(string Op, string[]? Ids = null, double? Hours = null, DashboardSettings? Settings = null);
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
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
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
public sealed class CollectorPipeServer(DashboardState state, HistoryStore db, ILogger<CollectorPipeServer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The collector accepts connections from local authenticated Windows users.
        // There are deliberately no file-system/process/fan-control commands.
        // Settings changes are validated by DashboardState.UpdateSettings.
        var acl = new PipeSecurity();
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
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
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
                    "saveSettings" when req.Settings != null => Save(req.Settings),
                    "events" => db.ReadEvents(),
                    "history" => new
                    {
                        series = db.ReadHistory((req.Ids ?? []).Where(s => s.Length <= 256).Distinct().Take(12).ToArray(),
                            Math.Clamp(req.Hours ?? 1, 0.1, 24 * 30))
                    },
                    "diagnostics" => Diagnostics(),
                    "status" => new
                    {
                        app = "SensorDash", version = "0.2.0", mode = "service",
                        started = state.Started, now = DateTimeOffset.UtcNow,
                        sensorCount = state.Latest?.Sensors.Count ?? 0,
                        lastSample = state.Latest?.Timestamp, error = state.Latest?.Error,
                        storagePollSeconds = state.Settings.StoragePollSeconds
                    },
                    _ => new { rpcError = "Unknown or unsupported collector command." }
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, PipeProtocol.Json).AsMemory(), deadline.Token);
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
            isAdministrator = SecurityInfo.IsAdministrator(),
            storageEnabled = true,
            storagePollSeconds = state.Settings.StoragePollSeconds,
            storageDevices = storage,
            storageSensorCount = readings.Length,
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
public sealed class DashboardBridgeWorker(DashboardState display, ILogger<DashboardBridgeWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var json = await CollectorClient.AskAsync(new PipeCommand("live"), stoppingToken);
                var frame = json.Deserialize<SensorFrame>(PipeProtocol.Json);
                if (frame != null) display.Publish(frame);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Collector not available yet");
                display.Publish(new SensorFrame(DateTimeOffset.UtcNow, [], "Collector service unavailable. Check Windows Services → SensorDash Collector."));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
public sealed class SensorDashTray : ApplicationContext
{
    private readonly NotifyIcon _icon;
    public SensorDashTray(IHostApplicationLifetime lifetime)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open SensorDash", null, (_, _) => Open());
        menu.Items.Add("Collector service: SensorDashCollector", null, (_, _) => Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit tray and dashboard", null, (_, _) =>
        {
            _icon.Visible = false;
            lifetime.StopApplication();
            ExitThread();
        });
        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SensorDash — Local Hardware Telemetry",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => Open();
    }
    private static void Open()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://127.0.0.1:8765") { UseShellExecute = true }); }
        catch { }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _icon.Visible = false; _icon.Dispose(); }
        base.Dispose(disposing);
    }
}
