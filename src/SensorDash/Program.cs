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
    try { WorkspaceSelfTest.Run(); FanControlSelfTest.Run(); FanCommissioningSelfTest.Run(); FanProfileReviewSelfTest.Run(); CoolingEventRecorderSelfTest.Run(); FanResumeSelfTest.Run(); HistoryStatisticsSelfTest.Run(); HistoryRetentionSelfTest.Run(); CoolingElectricalSelfTest.Run(); CoolingPresetCatalog.SelfTest(); PawnIoMonitor.SelfTest(); TelemetryExportSelfTest.Run(); TelemetryConfigurationSelfTest.Run(); TelemetryMetadataSelfTest.Run(); AccessoryPowerTelemetrySelfTest.Run(); }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "SensorDash-workspace-selftest-error.txt"), ex.ToString());
        Environment.ExitCode = 1;
        return;
    }
    return;
}

// An explicit UAC-elevated helper sends exactly one privileged fan-control
// command to an administrator-only pipe, then exits. There is deliberately no
// unprivileged fan-control operation on the telemetry pipe.
if (args.Contains("--fan-admin-command", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var at=Array.FindIndex(args,a=>a.Equals("--fan-admin-command",StringComparison.OrdinalIgnoreCase));
        if(at<0||at+1>=args.Length)throw new ArgumentException("Missing command payload.");
        var decoded=Convert.FromBase64String(args[at+1]);
        if(decoded.Length>4096)throw new ArgumentException("Command too large.");
        var command=JsonSerializer.Deserialize<FanCommand>(decoded, JsonSerializerOptions.Web);
        await AdminFanControlClient.SendAsync(command ?? throw new ArgumentException("Invalid command."));
    }
    catch(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(),"SensorDash-Fan-Admin.log"),
            DateTimeOffset.UtcNow+" "+ex.GetType().Name+": "+ex.Message+Environment.NewLine); }catch{}
        Environment.ExitCode=1;
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
            services.AddSingleton(new FanControlRuntime(data));
            // If telemetry is enabled, build its timestamp index before fan
            // collection starts. Otherwise no telemetry work is performed.
            services.AddHostedService<TelemetryExportWorker>();
            services.AddHostedService<SensorWorker>();
            services.AddHostedService<SimulationWorker>();
            services.AddHostedService<CollectorPipeServer>();
            services.AddHostedService<AdminFanControlPipeServer>();
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
// The desktop process only serves loopback minimal APIs and embedded assets.
// SlimBuilder avoids unrelated MVC/configuration defaults in the tray host;
// the elevated collector keeps its normal Windows Service host.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
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
app.MapGet("/fan-commissioning.js", () => Resource("fan-commissioning.js", "text/javascript; charset=utf-8"));
app.MapGet("/fan-control-ux.js", () => Resource("fan-control-ux.js", "text/javascript; charset=utf-8"));
app.MapGet("/fan-control-debug.js", () => Resource("fan-control-debug.js", "text/javascript; charset=utf-8"));
app.MapGet("/workspace-locks.js", () => Resource("workspace-locks.js", "text/javascript; charset=utf-8"));
app.MapGet("/fan-feedback.js", () => Resource("fan-feedback.js", "text/javascript; charset=utf-8"));
app.MapGet("/cooling-insights.js", () => Resource("cooling-insights.js", "text/javascript; charset=utf-8"));
app.MapGet("/cooling-electrical.js", () => Resource("cooling-electrical.js", "text/javascript; charset=utf-8"));
app.MapGet("/driver-status.js", () => Resource("driver-status.js", "text/javascript; charset=utf-8"));
app.MapGet("/style.css", () => Resource("style.css", "text/css; charset=utf-8"));
app.MapGet("/api/cooling-presets", () => Results.Ok(CoolingPresetCatalog.Items));
app.MapGet("/api/status", (DashboardState state) => Results.Ok(new
{
    app = "SensorDash", version = typeof(DashboardState).Assembly.GetName().Version?.ToString(3) ?? "unknown", mode = "desktop",
    collectorOnline = state.Latest is { Error: null },
    localOnly = true, started = state.Started, now = DateTimeOffset.UtcNow,
    pawnIo = state.Latest?.PawnIo,
    sensorCount = state.Latest?.Sensors.Count ?? 0,
    process = ProcessTelemetry.Snapshot(),
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
// Configuration changes are local to the Windows collector. No HTTP endpoint
// ever returns the bearer token or certificate PEM. Never trust cross-origin
// pages with service-owned secrets.
app.MapGet("/api/telemetry-config", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("telemetrySettings"), ct));
app.MapPut("/api/telemetry-config", async (HttpContext ctx) =>
{
    if (ctx.Request.Headers.Origin.ToString() != "http://127.0.0.1:8765" ||
        ctx.Request.Headers["X-SensorDash-Action"].ToString() != "telemetry-config" ||
        ctx.Request.ContentType is not string mime ||
        !mime.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(403);
    if (ctx.Request.ContentLength is > 24_576)
        return Results.StatusCode(413);
    try
    {
        var update = await JsonSerializer.DeserializeAsync<TelemetryConfigurationUpdate>(
            ctx.Request.Body, JsonSerializerOptions.Web, ctx.RequestAborted);
        return update is null
            ? Results.BadRequest("Invalid telemetry configuration.")
            : await ProxyAsync(new PipeCommand("saveTelemetrySettings", Telemetry: update), ctx.RequestAborted);
    }
    catch (JsonException) { return Results.BadRequest("Invalid telemetry configuration JSON."); }
});
app.MapGet("/api/events", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("events"), ct));
app.MapGet("/api/simulation", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("simulation"), ct));
app.MapGet("/api/fan-control", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("fanOutputs"), ct));
app.MapGet("/api/fan-control/debug", async (CancellationToken ct) =>
    await ProxyAsync(new PipeCommand("fanDebug"), ct));
app.MapPost("/api/fan-control/authorize", async (HttpContext context) =>
{
    // Browser mutations require same-origin JSON with a non-simple custom
    // header. Cross-origin pages cannot satisfy this through ordinary forms.
    if(context.Request.Headers.Origin.ToString() != "http://127.0.0.1:8765" ||
       context.Request.Headers["X-SensorDash-Action"].ToString() != "fan-control" ||
       context.Request.ContentType is not string mime ||
       !mime.StartsWith("application/json",StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(403);
    FanCommand? command;
    try
    {
        command=await JsonSerializer.DeserializeAsync<FanCommand>(
            context.Request.Body,JsonSerializerOptions.Web,context.RequestAborted);
    }
    catch(JsonException){return Results.BadRequest("Invalid control request.");}
    if(command is null || command.Action is not ("arm" or "release" or "start-profile" or "stop-profile" or "stop-all") ||
       command.ChannelId.Length>256 || command.CurveId.Length>80 ||
       command.DurationSeconds is < 15 or > FanControlRuntime.MaxSessionSeconds ||
       !double.IsFinite(command.Percent) ||
       ((command.Action=="arm" || command.Action=="start-profile") &&
           !command.ConfirmExclusiveOwnership) || command.ProfileId.Length>80)
        return Results.BadRequest("Invalid or unconfirmed control request.");
    try
    {
        var process=new ProcessStartInfo(Environment.ProcessPath ??
            throw new InvalidOperationException("Cannot locate SensorDash executable"))
        {
            UseShellExecute=true,Verb="runas"
        };
        process.ArgumentList.Add("--fan-admin-command");
        process.ArgumentList.Add(Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(command,JsonSerializerOptions.Web)));
        Process.Start(process);
        return Results.Accepted(value:new { message="Windows elevation requested. Check live status for the actual result." });
    }
    catch(System.ComponentModel.Win32Exception)
    {
        return Results.Problem("Elevation was cancelled or unavailable.",statusCode:403);
    }
});
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
    using var context = new SensorDashTray(app.Services.GetRequiredService<DashboardState>(),app.Lifetime);
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

// Lightweight on-demand process diagnostics. No hardware reads or GC collection are triggered.
public static class ProcessTelemetry
{
    public static object Snapshot()
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(false),
            threadCount = process.Threads.Count
        };
    }
}
public sealed record SensorReading(string Id, string Name, string Device, string DeviceType, string Type, double? Value, DateTimeOffset? ObservedAt = null, string? DeviceId = null);
public sealed record HardwareDevice(string Id, string Name, string Type, int SensorCount);
public sealed record SensorFrame(DateTimeOffset Timestamp, List<SensorReading> Sensors, string? Error, List<HardwareDevice>? Devices = null, PawnIoStatus? PawnIo = null);
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
    public int RetentionDays { get; set; } = 30; // configurable through 3 years
    public List<CoolingSystemConfig> CoolingSystems { get; set; } = [];
    public List<TrayWidgetConfig> TrayWidgets { get; set; } = [];
    public List<FanProfileConfig> FanProfiles { get; set; } = [];
    public bool FanControlLocked { get; set; } // UI editor lock only; not hardware authorization.
    // Advisory mapping metadata never authorizes a hardware command.
    public List<FanChannelMapping> FanChannelMappings { get; set; } = [];
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
        requested.RetentionDays = Math.Clamp(requested.RetentionDays, 1, 1095);
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
public sealed partial class HistoryStore {
    private readonly string _connectionString;
    private readonly string _path;
    public HistoryStore(string path) {
        _path=path;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        // Enable reclamation once, before collection/control starts. Existing
        // databases need a one-time rebuild to add SQLite's pointer map.
        cmd.CommandText = "PRAGMA auto_vacuum";
        var vacuumMode = Convert.ToInt32(cmd.ExecuteScalar());
        if (vacuumMode != 1) {
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            var existing = Convert.ToInt64(cmd.ExecuteScalar()) > 0;
            cmd.CommandText = "PRAGMA auto_vacuum=FULL";
            cmd.ExecuteNonQuery();
            if (existing && vacuumMode == 0) {
                cmd.CommandText = "VACUUM";
                cmd.ExecuteNonQuery();
            }
        }
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples(sensor TEXT NOT NULL, ts INTEGER NOT NULL, value REAL NOT NULL, PRIMARY KEY(sensor, ts));
            CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, severity TEXT, sensor TEXT, message TEXT, value REAL);
            CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts);
            CREATE TABLE IF NOT EXISTS accessory_power (
                system TEXT NOT NULL, component TEXT NOT NULL,
                ts INTEGER NOT NULL, watts REAL NOT NULL,
                source TEXT NOT NULL, revision TEXT NOT NULL,
                PRIMARY KEY(system,component,ts)
            );
            CREATE INDEX IF NOT EXISTS idx_accessory_power_export
                ON accessory_power(ts,system,component);
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
        // Preserve real SSD/NVMe data channels for Pi storage dashboards.
        // This remains a bounded archive/retry spool, not a second viewer.
        if (config.HistoryMode == "balanced" && sensor.DeviceType == "Storage" &&
            (sensor.Type is "Temperature" or "Load" or "Throughput" or "Data" or "Level"))
            return true;
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
    internal static long StatsGapMs(int saveEverySeconds) =>
        Math.Clamp((long)saveEverySeconds*3000,90000,360000);

    // Bounded number of requested sensors. Stream ordered raw values through
    // one accumulator per sensor; never materialize long history arrays or
    // treat gaps as continuously consumed energy.
    internal List<HistoricalSensorStats> ReadStatistics(string[] ids,double hours,
        IReadOnlyDictionary<string,string> sensorTypes,int cadenceSeconds)
    {
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var from=now-(long)(Math.Clamp(hours,0.1,26280)*3600000);
        var result=new List<HistoricalSensorStats>(Math.Min(ids.Length,8));
        using var connection=Open();
        using var command=connection.CreateCommand();
        command.CommandText="SELECT ts,value FROM samples WHERE sensor=$id AND ts >= $from AND ts <= $to ORDER BY ts";
        var id=command.Parameters.Add("$id",SqliteType.Text);
        command.Parameters.AddWithValue("$from",from);
        command.Parameters.AddWithValue("$to",now);
        foreach(var sensorId in ids.Where(x=>x.Length is >0 and <=256).Distinct().Take(8))
        {
            var type=sensorTypes.GetValueOrDefault(sensorId) ?? "Unknown";
            var stats=new HistoryStatsAccumulator(sensorId,type,from,now,StatsGapMs(cadenceSeconds));
            id.Value=sensorId;
            using(var reader=command.ExecuteReader())
                while(reader.Read())stats.Add(reader.GetInt64(0),reader.GetDouble(1));
            result.Add(stats.Finish());
        }
        return result;
    }

    // Actual filesystem allocation, including SQLite WAL/SHM. Per-sensor
    // disk allocation cannot be assigned exactly: SQLite pages are shared.
    // Report sample row counts and proportional shares instead.
    public object ReadStorage(SensorFrame? latest,DashboardSettings settings)
    {
        long Size(string file)
        {
            try{return File.Exists(file)?new FileInfo(file).Length:0;}
            catch{return 0;}
        }
        using var c=Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="SELECT sensor,COUNT(*),MIN(ts),MAX(ts) FROM samples GROUP BY sensor ORDER BY COUNT(*) DESC";
        var types=(latest?.Sensors??[]).ToDictionary(x=>x.Id,x=>x);
        var grouped=new List<(string Sensor,long Count,long Oldest,long Newest,string Device,string SensorName)>();
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
            {
                var key=reader.GetString(0);
                var match=types.GetValueOrDefault(key);
                grouped.Add((key,reader.GetInt64(1),reader.GetInt64(2),reader.GetInt64(3),
                    match?.Device ?? "Previously detected / unknown",
                    settings.Aliases.GetValueOrDefault(key)??match?.Name??key));
            }
        var sensorCount=grouped.Sum(x=>x.Count);
        var devices=grouped.GroupBy(x=>x.Device,StringComparer.Ordinal)
            .Select(g=>new{device=g.Key,samples=g.Sum(x=>x.Count),
                sensors=g.Count()}).OrderByDescending(g=>g.samples).ToArray();
        using var pages=c.CreateCommand();
        pages.CommandText="PRAGMA page_size; PRAGMA page_count; PRAGMA freelist_count;";
        // Keep low-level storage counters available for diagnostics.
        long ReadPragma(string name)
        {
            using var pragma=c.CreateCommand();
            pragma.CommandText="PRAGMA "+name;
            return Convert.ToInt64(pragma.ExecuteScalar());
        }
        var pageSize=ReadPragma("page_size");
        var free=ReadPragma("freelist_count");
        var pageCount=ReadPragma("page_count");
        long? oldest=grouped.Count==0?null:grouped.Min(x=>x.Oldest);
        long? newest=grouped.Count==0?null:grouped.Max(x=>x.Newest);
        return new {
            retentionDays=settings.RetentionDays,saveEverySeconds=settings.SaveEverySeconds,
            mode=settings.HistoryMode,dbBytes=Size(_path),walBytes=Size(_path+"-wal"),
            shmBytes=Size(_path+"-shm"),freePageBytes=free*pageSize,
            allocatedPageBytes=pageCount*pageSize,sampleCount=sensorCount,
            sensorCount=grouped.Count,oldest,newest,
            devices, sensors=grouped.Select(x=>new {
                id=x.Sensor,name=x.SensorName,device=x.Device,count=x.Count,
                oldest=x.Oldest,newest=x.Newest
            }).ToArray()
        };
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
        cmd.CommandText = "DELETE FROM samples WHERE ts < $before; DELETE FROM events WHERE ts < $before; DELETE FROM accessory_power WHERE ts < $before";
        cmd.Parameters.AddWithValue("$before", before);
        using (var transaction = conn.BeginTransaction()) {
            cmd.Transaction = transaction;
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
        // Auto-vacuum reclaims deleted pages at commit. Do not wait on live
        // readers to truncate WAL; a later cleanup can finish that checkpoint.
        using var checkpoint = conn.CreateCommand();
        checkpoint.CommandText = "PRAGMA busy_timeout";
        var busyTimeout = Convert.ToInt32(checkpoint.ExecuteScalar());
        try {
            checkpoint.CommandText = "PRAGMA busy_timeout=0; PRAGMA wal_checkpoint(TRUNCATE)";
            checkpoint.ExecuteNonQuery();
        } finally {
            checkpoint.CommandText = $"PRAGMA busy_timeout={busyTimeout}";
            checkpoint.ExecuteNonQuery();
        }
    }
}
public sealed class SensorWorker(DashboardState state, HistoryStore db, FanControlRuntime controls, ILogger<SensorWorker> logger) : BackgroundService {
    private readonly Dictionary<string, bool> _alarms = new();
    private Computer? _computer;
    private readonly PawnIoMonitor _pawnIo = new();
    private string? _activeCollectionSignature;
    private DateTimeOffset _lastSaved = DateTimeOffset.MinValue;
    private DateTimeOffset _lastStorage = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private int _lastRetentionDays;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            var cfg = state.Settings;
            var driver = _pawnIo.Read(cfg);
            try {
                var collectionSignature = HardwareCollection.Signature(cfg);
                if (_computer != null && _activeCollectionSignature != collectionSignature) {
                    // LHM's Computer family flags are applied during discovery.
                    // Reopen only when those flags change, not when users toggle
                    // a sensor's visibility, history or a specific device's polls.
                    controls.ReleaseBeforeClose(_computer,"Collection changed; restored defaults.");
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
                var frame = new SensorFrame(now, readings, null, devices, driver);
                controls.Tick(_computer, frame, cfg, now);
                state.Publish(frame);
                if ((now - _lastSaved).TotalSeconds >= cfg.SaveEverySeconds) {
                    db.Record(frame, cfg);
                    // Derived electrical history is observational only. A failed
                    // optional computation/storage cannot interrupt fan control.
                    try { db.RecordAccessoryPower(frame, cfg); }
                    catch (Exception electricalError) {
                        logger.LogWarning(electricalError, "Accessory power archive failed; control is unchanged.");
                    }
                    CheckEvents(frame, cfg);
                    _lastSaved = now;
                }
                if ((now - _lastCleanup).TotalHours >= 1 || cfg.RetentionDays != _lastRetentionDays) {
                    db.Cleanup(cfg.RetentionDays); _lastCleanup = now; _lastRetentionDays = cfg.RetentionDays;
                }
            } catch (Exception ex) {
                logger.LogError(ex, "Sensor collection failed");
                controls.ReleaseBeforeClose(_computer,"Hardware collection failed; release attempted.");
                try { File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SensorDash", "SensorDash.log"), $"{DateTimeOffset.Now}: {ex}\n"); } catch { }
                state.Publish(new SensorFrame(DateTimeOffset.UtcNow, [], ex.GetType().Name + ": " + ex.Message, PawnIo: driver));
                try { _computer?.Close(); } catch { }
                _computer = null;
                try { await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken); } catch (OperationCanceledException) { }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(controls.IsActive
                ? 1 : Math.Clamp(cfg.FastPollSeconds, 1, 30)), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        controls.ReleaseBeforeClose(_computer,"Collector stopped; restore firmware.");
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
