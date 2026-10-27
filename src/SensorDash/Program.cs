using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting.WindowsServices;

// Pure synthetic tests run without opening any hardware or web endpoints.
if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    try { WorkspaceSelfTest.Run(); }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "SensorDash-workspace-selftest-error.txt"), ex.ToString());
        Environment.ExitCode = 1;
        return;
    }
    return;
}

// Same signed executable, two separate Windows processes.
// Windows Service (--collector) owns elevated sensor hardware access and the database.
// Desktop (default) owns the tray icon + loopback web server, never opens the hardware.
if (args.Contains("--collector", StringComparer.OrdinalIgnoreCase))
{
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorDash");
    Directory.CreateDirectory(data);
    var service = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options => options.ServiceName = "SensorDashCollector")
        .ConfigureServices(services =>
        {
            services.AddSingleton(new DashboardState(data));
            services.AddSingleton(new HistoryStore(Path.Combine(data, "telemetry.db")));
            services.AddSingleton<SimulationState>();
            services.AddHostedService<SensorWorker>();
            services.AddHostedService<SimulationWorker>();
            services.AddHostedService<CollectorPipeServer>();
        }).Build();
    await service.RunAsync();
    return;
}

// An existing tray process owns localhost:8765. Double-click only opens its UI.
try
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(750) };
    using var response = await probe.GetAsync("http://127.0.0.1:8765/api/status");
    if (response.IsSuccessStatusCode)
    {
        if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:8765") { UseShellExecute = true }); } catch { }
        return;
    }
}
catch (HttpRequestException) { }
catch (TaskCanceledException) { }

var userDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SensorDash");
Directory.CreateDirectory(userDir);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls("http://127.0.0.1:8765");
builder.Services.AddSingleton(new DashboardState(userDir));
builder.Services.AddHostedService<DashboardBridgeWorker>();
var app = builder.Build();
IResult Resource(string name, string mediaType)
{
    var resource = typeof(DashboardState).Assembly.GetManifestResourceStream("SensorDash.Web." + name);
    return resource is null
        ? Results.Problem("Missing embedded dashboard resource: " + name, statusCode: 500)
        : Results.Stream(resource, contentType: mediaType);
}
app.MapGet("/", () => Resource("index.html", "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Resource("index.html", "text/html; charset=utf-8"));
app.MapGet("/app.js", () => Resource("app.js", "text/javascript; charset=utf-8"));
app.MapGet("/workspace.js", () => Resource("workspace.js", "text/javascript; charset=utf-8"));
app.MapGet("/style.css", () => Resource("style.css", "text/css; charset=utf-8"));
app.MapGet("/api/status", (DashboardState state) => Results.Ok(new
{
    app = "SensorDash", version = typeof(DashboardState).Assembly.GetName().Version?.ToString(3) ?? "unknown", mode = "desktop",
    collectorOnline = state.Latest is { Error: null },
    localOnly = true, started = state.Started, now = DateTimeOffset.UtcNow,
    sensorCount = state.Latest?.Sensors.Count ?? 0,
    lastSample = state.Latest?.Timestamp,
    error = state.Latest?.Error
}));
app.MapGet("/api/live", (DashboardState state) =>
    Results.Ok(state.Latest ?? new SensorFrame(DateTimeOffset.UtcNow, [], "Connecting to SensorDash Collector...")));
app.MapGet("/api/diagnostics", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("diagnostics"), ct));
app.MapGet("/api/settings", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("settings"), ct));
app.MapGet("/api/collection-groups", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("collectionGroups"), ct));
app.MapPut("/api/settings", async (HttpContext ctx) =>
{
    try
    {
        var updated = await JsonSerializer.DeserializeAsync<DashboardSettings>(
            ctx.Request.Body, JsonSerializerOptions.Web, ctx.RequestAborted);
        return updated == null
            ? Results.BadRequest("Invalid settings.")
            : await ProxyAsync(new PipeCommand("saveSettings", Settings: updated), ctx.RequestAborted);
    }
    catch (JsonException) { return Results.BadRequest("Invalid settings JSON."); }
});
app.MapGet("/api/history", async (string? ids, double? hours, CancellationToken ct) =>
{
    var sensors = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct().Take(12).ToArray();
    return await ProxyAsync(new PipeCommand("history", sensors, hours), ct);
});
app.MapGet("/api/events", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("events"), ct));
app.MapGet("/api/simulation", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("simulation"), ct));
async Task<IResult> ProxyAsync(PipeCommand request, CancellationToken ct)
{
    try { return Results.Json(await CollectorClient.AskAsync(request, ct), JsonSerializerOptions.Web); }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
        return Results.Problem("Collector service unavailable: " + ex.Message, statusCode: 503);
    }
}
app.MapGet("/api/stream", async (HttpContext ctx, DashboardState state) =>
{
    ctx.Response.Headers.CacheControl = "no-cache, no-transform";
    ctx.Response.ContentType = "text/event-stream";
    var (id, reader) = state.Subscribe();
    try
    {
        if (state.Latest is { } initial)
            await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(initial, JsonSerializerOptions.Web) + "\n\n", ctx.RequestAborted);
        await foreach (var snapshot in reader.ReadAllAsync(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync("data: " + snapshot + "\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally { state.Unsubscribe(id); }
});

// A dedicated STA UI thread supplies the notification-area menu.
// The ASP.NET host runs asynchronously and never requires elevated privileges.
var trayThread = new Thread(() =>
{
    Application.EnableVisualStyles();
    using var context = new SensorDashTray(app.Lifetime);
    Application.Run(context);
}) { IsBackground = true, Name = "SensorDash Tray" };
trayThread.SetApartmentState(ApartmentState.STA);
trayThread.Start();
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            try { Process.Start(new ProcessStartInfo("http://127.0.0.1:8765") { UseShellExecute = true }); }
            catch { }
        });
});
await app.RunAsync();

public sealed record SensorReading(string Id, string Name, string Device, string DeviceType, string Type, double? Value, DateTimeOffset? ObservedAt = null, string? DeviceId = null);
public sealed record HardwareDevice(string Id, string Name, string Type, int SensorCount);
public sealed record SensorFrame(DateTimeOffset Timestamp, List<SensorReading> Sensors, string? Error, List<HardwareDevice>? Devices = null);
public static class SecurityInfo {
    public static bool IsAdministrator() {
        try {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch { return false; }
    }
}
public sealed class DashboardSettings {
    public int FastPollSeconds { get; set; } = 2;
    public int StoragePollSeconds { get; set; } = 10;
    public int SaveEverySeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 30;
    public List<CoolingSystemConfig> CoolingSystems { get; set; } = [];
    public List<WorkspaceDashboardConfig> Dashboards { get; set; } = [
        new() { Id = "system", Name = "System dashboard" }
    ];
    public List<TrayWidgetConfig> TrayWidgets { get; set; } = [];
    public List<VirtualSensorConfig> VirtualSensors { get; set; } = [];
    public List<SimulatedFanCurveConfig> SimulatedFanCurves { get; set; } = [];
    // Visibility affects default views and sensor pickers, not collection,
    // history, configured charts, alerts or simulated curves.
    public List<string> HiddenSensorIds { get; set; } = [];
    public List<string> HiddenDeviceIds { get; set; } = [];
    public List<string> HiddenHardwareTypes { get; set; } = [];
    // What we archive is independent of what we display.
    // 'in-use' writes only explicitly referenced measurements.
    public string HistoryMode { get; set; } = "balanced";
    public List<string> ExcludedHistoryHardwareTypes { get; set; } = [];
    // Hardware-family switches control LHM discovery before Computer.Open().
    // Device IDs let users pause a specific detected device's Update() call.
    // Neither setting changes a sensor's visibility preferences.
    public List<string> DisabledCollectionGroups { get; set; } = [];
    public List<string> DisabledCollectionDeviceIds { get; set; } = [];
    public Dictionary<string, string> Aliases { get; set; } = new();
    public List<string> PinnedSensors { get; set; } = [];
    public Dictionary<string, double> HighThresholds { get; set; } = new();
}
public sealed class DashboardState {
    private readonly string _settingsPath;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _listeners = new();
    private DashboardSettings _settings;
    private SensorFrame? _latest;
    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
    public SensorFrame? Latest => Volatile.Read(ref _latest);
    private long _lastStorageTicks;
    public DateTimeOffset? LastStoragePoll => Interlocked.Read(ref _lastStorageTicks) is long ticks && ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    public void MarkStoragePoll(DateTimeOffset time) => Interlocked.Exchange(ref _lastStorageTicks, time.UtcTicks);
    public DashboardSettings Settings => Volatile.Read(ref _settings);
    public DashboardState(string dir) {
        _settingsPath = Path.Combine(dir, "settings.json");
        try { _settings = JsonSerializer.Deserialize<DashboardSettings>(File.ReadAllText(_settingsPath), JsonSerializerOptions.Web) ?? new(); }
        catch { _settings = new DashboardSettings(); }
    }
    public void UpdateSettings(DashboardSettings requested) {
        requested.FastPollSeconds = Math.Clamp(requested.FastPollSeconds, 1, 30);
        requested.StoragePollSeconds = Math.Clamp(requested.StoragePollSeconds, 5, 120);
        requested.SaveEverySeconds = Math.Clamp(requested.SaveEverySeconds, 5, 120);
        requested.RetentionDays = Math.Clamp(requested.RetentionDays, 1, 365);
        requested.Aliases ??= new();
        requested.PinnedSensors ??= [];
        requested.HighThresholds ??= new();
        requested.Aliases = requested.Aliases.Where(kv => kv.Key.Length <= 256).Take(500).ToDictionary(kv => kv.Key, kv => kv.Value.Length > 120 ? kv.Value[..120] : kv.Value);
        requested.PinnedSensors = requested.PinnedSensors.Where(s => s.Length <= 256).Distinct().Take(100).ToList();
        requested.HighThresholds = requested.HighThresholds.Where(kv => kv.Key.Length <= 256 && double.IsFinite(kv.Value)).Take(100).ToDictionary();
        WorkspaceLogic.Normalize(requested);
        var temp = _settingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(requested, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
        File.Move(temp, _settingsPath, true);
        Volatile.Write(ref _settings, requested);
    }
    public void Publish(SensorFrame frame) {
        Volatile.Write(ref _latest, frame);
        var message = JsonSerializer.Serialize(frame, JsonSerializerOptions.Web);
        foreach (var listener in _listeners.Values) listener.Writer.TryWrite(message);
    }
    public (Guid Id, ChannelReader<string> Reader) Subscribe() {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var id = Guid.NewGuid(); _listeners[id] = channel;
        return (id, channel.Reader);
    }
    public void Unsubscribe(Guid id) {
        if (_listeners.TryRemove(id, out var channel)) channel.Writer.TryComplete();
    }
}
public sealed record HistoryPoint(long T, double V);
public sealed record HistorySeries(string Id, List<HistoryPoint> Points);
public sealed record MonitorEvent(long T, string Severity, string Sensor, string Message, double? Value);
public sealed class HistoryStore {
    private readonly string _connectionString;
    public HistoryStore(string path) {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples(sensor TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL, PRIMARY KEY(sensor, ts));
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, severity TEXT, sensor TEXT, message TEXT, value REAL);
            CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    public void Record(SensorFrame frame, DashboardSettings settings) {
        var used = settings.HistoryMode == "in-use" ? WorkspaceLogic.ReferencedSensorIds(settings) : null;
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO samples(sensor, ts, value) VALUES ($id, $ts, $v)";
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
        var val = cmd.Parameters.Add("$v", SqliteType.Real);
        ts.Value = frame.Timestamp.ToUnixTimeMilliseconds();
        foreach (var sensor in frame.Sensors) {
            if (sensor.Value is not double v || !double.IsFinite(v) ||
                !ShouldRecord(sensor, settings, used)) continue;
            id.Value = sensor.Id; val.Value = v; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal static bool ShouldRecord(SensorReading sensor, DashboardSettings config, HashSet<string>? used = null) {
        if (config.ExcludedHistoryHardwareTypes.Contains(sensor.DeviceType, StringComparer.OrdinalIgnoreCase))
            return false;
        if (config.HistoryMode == "all") return true;
        if (config.HistoryMode == "in-use")
            return (used ?? WorkspaceLogic.ReferencedSensorIds(config)).Contains(sensor.Id);
        if (config.PinnedSensors.Contains(sensor.Id)) return true;
        if (sensor.Type is "Temperature" or "Fan" or "Control" or "Power" or "Throughput") return true;
        if (sensor.Type == "Load" &&
            !(sensor.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase) ||
              sensor.Name.StartsWith("D3D ", StringComparison.OrdinalIgnoreCase))) return true;
        return sensor.Type == "Clock" &&
            (sensor.Name.Contains("Average") || sensor.Name is "GPU Core" or "GPU Memory");
    }
    public List<HistorySeries> ReadHistory(string[] sensorIds, double hours) {
        var result = new List<HistorySeries>();
        long start = DateTimeOffset.UtcNow.AddHours(-hours).ToUnixTimeMilliseconds();
        long step = Math.Max(10000, (long)(hours * 3600000 / 500));
        using var conn = Open();
        foreach (var sensorId in sensorIds) {
            var points = new List<HistoryPoint>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
              SELECT (ts / $bucket) * $bucket AS bucket, AVG(value)
              FROM samples WHERE sensor=$id AND ts >= $from
              GROUP BY bucket ORDER BY bucket LIMIT 2000
              """;
            cmd.Parameters.AddWithValue("$bucket", step);
            cmd.Parameters.AddWithValue("$id", sensorId);
            cmd.Parameters.AddWithValue("$from", start);
            using var r = cmd.ExecuteReader();
            while (r.Read()) points.Add(new HistoryPoint(r.GetInt64(0), r.GetDouble(1)));
            result.Add(new HistorySeries(sensorId, points));
        }
        return result;
    }
    public void Event(string severity, string sensor, string message, double? value) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO events(ts,severity,sensor,message,value) VALUES($ts,$severity,$sensor,$message,$value)";
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$severity", severity);
        cmd.Parameters.AddWithValue("$sensor", sensor);
        cmd.Parameters.AddWithValue("$message", message);
        cmd.Parameters.AddWithValue("$value", value is double v ? v : DBNull.Value);
        cmd.ExecuteNonQuery();
    }
    public List<MonitorEvent> ReadEvents() {
        var output = new List<MonitorEvent>();
        using var conn = Open(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ts,severity,sensor,message,value FROM events ORDER BY ts DESC LIMIT 100";
        using var r = cmd.ExecuteReader();
        while (r.Read()) output.Add(new MonitorEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetDouble(4)));
        return output;
    }
    public void Cleanup(int retentionDays) {
        var before = DateTimeOffset.UtcNow.AddDays(-retentionDays).ToUnixTimeMilliseconds();
        using var conn = Open(); using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE ts < $before; DELETE FROM events WHERE ts < $before";
        cmd.Parameters.AddWithValue("$before", before); cmd.ExecuteNonQuery();
    }
}
public sealed class SensorWorker(DashboardState state, HistoryStore db, ILogger<SensorWorker> logger) : BackgroundService {
    private readonly Dictionary<string, bool> _alarms = new();
    private Computer? _computer;
    private string? _activeCollectionSignature;
    private DateTimeOffset _lastSaved = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStorage = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            var cfg = state.Settings;
            try {
                var collectionSignature = HardwareCollection.Signature(cfg);
                if (_computer != null && _activeCollectionSignature != collectionSignature) {
                    // LHM's Computer family flags are applied during discovery.
                    // Reopen only when those flags change, not when users toggle
                    // a sensor's visibility, history or a specific device's polls.
                    try { _computer.Close(); } catch { }
                    _computer = null;
                }
                if (_computer == null) {
                    _computer = HardwareCollection.CreateComputer(cfg);
                    _computer.Open();
                    _activeCollectionSignature = collectionSignature;
                }
                var now = DateTimeOffset.UtcNow;
                var refreshStorage = (now - _lastStorage).TotalSeconds >= cfg.StoragePollSeconds;
                if (refreshStorage) { _lastStorage = now; state.MarkStoragePoll(now); }
                var readings = new List<SensorReading>();
                var devices = new List<HardwareDevice>();
                var pausedDevices = cfg.DisabledCollectionDeviceIds.ToHashSet(StringComparer.Ordinal);
                foreach (var hardware in _computer.Hardware)
                    Scan(hardware, readings, devices, refreshStorage, now, _lastStorage,
                        pausedDevices);
                var baseFrame = new SensorFrame(now, readings, null, devices);
                readings.AddRange(WorkspaceLogic.VirtualReadings(baseFrame, cfg.VirtualSensors));
                var frame = new SensorFrame(now, readings, null, devices);
                state.Publish(frame);
                if ((now - _lastSaved).TotalSeconds >= cfg.SaveEverySeconds) {
                    db.Record(frame, cfg);
                    CheckEvents(frame, cfg);
                    _lastSaved = now;
                }
                if ((now - _lastCleanup).TotalHours >= 1) {
                    db.Cleanup(cfg.RetentionDays); _lastCleanup = now;
                }
            } catch (Exception ex) {
                logger.LogError(ex, "Sensor collection failed");
                try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SensorDash", "SensorDash.log"), $"{DateTimeOffset.Now}: {ex}\n"); } catch { }
                state.Publish(new SensorFrame(DateTimeOffset.UtcNow, [], ex.GetType().Name + ": " + ex.Message));
                try { _computer?.Close(); } catch { }
                _computer = null;
                try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); } catch (OperationCanceledException) { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(cfg.FastPollSeconds, 1, 30)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        try { _computer?.Close(); } catch { }
    }
    private static void Scan(IHardware hardware, List<SensorReading> target, List<HardwareDevice> devices,
        bool refreshStorage, DateTimeOffset now, DateTimeOffset lastStorage,
        HashSet<string> pausedDeviceIds, bool parentPaused = false) {
        // The device remains discoverable and its sensor metadata stays visible.
        // But a paused device (or child of one) is never updated and has null
        // readings, so charts and simulation cannot mistake cached values for live data.
        var hardwareId = hardware.Identifier.ToString();
        var paused = parentPaused || pausedDeviceIds.Contains(hardwareId);
        if (!paused && (hardware.HardwareType != HardwareType.Storage || refreshStorage))
            hardware.Update();
        devices.Add(new HardwareDevice(hardwareId, hardware.Name,
            hardware.HardwareType.ToString(), hardware.Sensors.Count()));
        foreach (var sensor in hardware.Sensors) {
            double? value = !paused && sensor.Value is float x && float.IsFinite(x) ? x : null;
            target.Add(new SensorReading(sensor.Identifier.ToString(), sensor.Name, hardware.Name,
                hardware.HardwareType.ToString(), sensor.SensorType.ToString(), value,
                !paused ? (hardware.HardwareType == HardwareType.Storage ? lastStorage : now) : null,
                hardwareId));
        }
        foreach (var child in hardware.SubHardware)
            Scan(child, target, devices, refreshStorage, now, lastStorage,
                pausedDeviceIds, paused);
    }
    private void CheckEvents(SensorFrame frame, DashboardSettings cfg) {
        var byId = frame.Sensors.ToDictionary(s => s.Id, s => s);
        foreach (var (id, threshold) in cfg.HighThresholds) {
            if (!byId.TryGetValue(id, out var sensor) || sensor.Value is not double value) continue;
            var active = _alarms.GetValueOrDefault(id);
            if (!active && value >= threshold) {
                db.Event("warning", id, $"{cfg.Aliases.GetValueOrDefault(id, sensor.Name)} reached {value:0.#} (threshold {threshold:0.#})", value);
                _alarms[id] = true;
            } else if (active && value < threshold - 2) {
                db.Event("info", id, $"{cfg.Aliases.GetValueOrDefault(id, sensor.Name)} returned below threshold", value);
                _alarms[id] = false;
            }
        }

    }
}
