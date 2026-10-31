using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

// Outbound-only telemetry: no fan-control commands, no additional hardware polling.
// The existing local SQLite archive doubles as a bounded retry spool.
internal sealed class TelemetryExportOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string TokenFile { get; set; } = "";
    public string CaCertificateFile { get; set; } = "";
    public string HostLabel { get; set; } = "";

    public static TelemetryExportOptions Load(string path)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<TelemetryExportOptions>(
            File.ReadAllText(path), JsonSerializerOptions.Web) ?? new();
    }

    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/ingest" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Telemetry endpoint must be https://host:port/ingest.");
        if (!Path.IsPathFullyQualified(TokenFile) ||
            !Path.IsPathFullyQualified(CaCertificateFile))
            throw new InvalidDataException("Telemetry token and CA certificate paths must be absolute.");
    }
}

internal sealed record TelemetryCursor(long TimestampMs, string SensorId);
internal sealed record TelemetryRow(string SensorId, long TimestampMs, double Value);
internal sealed record ThresholdTelemetryEvent(long Id, long TimestampMs, string SensorId, string Severity);

public sealed partial class HistoryStore
{
    // Index only when explicitly enabling export. The existing primary key
    // starts with sensor, so it cannot efficiently page in timestamp order.
    // This one-time migration is performed at collector startup before
    // SensorWorker begins controlling hardware.
    // Only bounded, historical events are exported. Windows retains full
    // messages; the Pi stores severity, sensor ID and original timestamp.
    internal List<ThresholdTelemetryEvent> ReadThresholdEvents(long afterId, int limit = 128)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,ts,sensor,severity FROM events WHERE id>$id ORDER BY id LIMIT $limit";
        command.Parameters.AddWithValue("$id", afterId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 256));
        using var reader = command.ExecuteReader();
        var result = new List<ThresholdTelemetryEvent>();
        while (reader.Read())
            result.Add(new ThresholdTelemetryEvent(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3)));
        return result;
    }

    // Cheap filesystem byte counts, NOT a costly per-sensor GROUP BY. This
    // keeps the collector and fan-control sampling independent of monitoring.
    internal string StorageFootprint(string host, long nowMs)
    {
        long Size(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; } }
        var safeHost = TelemetryLineProtocol.Label(host, 80);
        return string.Join("", new[] {
            $"sensordash_spool,host={safeHost},file=database value={Size(_path)} {nowMs}\n",
            $"sensordash_spool,host={safeHost},file=wal value={Size(_path + "-wal")} {nowMs}\n",
            $"sensordash_spool,host={safeHost},file=shm value={Size(_path + "-shm")} {nowMs}\n"
        });
    }

    internal void EnsureTelemetryIndex()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE INDEX IF NOT EXISTS idx_samples_telemetry_export ON samples(ts, sensor)";
        command.ExecuteNonQuery();
    }

    internal long LatestTelemetryTimestamp()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(ts) FROM samples";
        var scalar = command.ExecuteScalar();
        return scalar is long stamp ? stamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    internal List<TelemetryRow> ReadTelemetryBatch(TelemetryCursor cursor, int requested = 384)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sensor, ts, value FROM samples
            WHERE (ts, sensor) > ($ts, $sensor)
            ORDER BY ts ASC, sensor ASC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$ts", cursor.TimestampMs);
        command.Parameters.AddWithValue("$sensor", cursor.SensorId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(requested, 1, 1024));
        var rows = new List<TelemetryRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var reading = reader.GetDouble(2);
            if (double.IsFinite(reading))
                rows.Add(new TelemetryRow(reader.GetString(0), reader.GetInt64(1), reading));
        }
        return rows;
    }
}

internal static class TelemetryLineProtocol
{
    // The companion intake intentionally accepts this restricted ASCII label alphabet.
    internal static string Label(string value, int maxLength)
    {
        var result = new StringBuilder();
        foreach (var c in value)
        {
            if (result.Length >= maxLength) break;
            result.Append(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' ||
                          c is '_' or '.' or ':' or '/' or '-' ? c : '_');
        }
        return result.Length == 0 ? "unknown" : result.ToString();
    }

    internal static string Serialize(IEnumerable<TelemetryRow> rows, string host,
        IReadOnlyDictionary<string, string> types)
    {
        var output = new StringBuilder();
        foreach (var row in rows)
        {
            // Millisecond Unix epoch timestamps; the intake sets precision=ms.
            output.Append("sensordash_reading,host=").Append(Label(host, 80))
                .Append(",sensor=").Append(Label(row.SensorId, 256))
                .Append(",kind=").Append(Label(types.GetValueOrDefault(row.SensorId, "Unknown"), 40))
                .Append(" value=").Append(row.Value.ToString("G17", CultureInfo.InvariantCulture))
                .Append(' ').Append(row.TimestampMs.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return output.ToString();
    }
}

internal static class ThresholdEventProtocol
{
    internal static string Serialize(IEnumerable<ThresholdTelemetryEvent> events, string host)
    {
        var h = TelemetryLineProtocol.Label(host, 80);
        var sb = new StringBuilder();
        foreach (var item in events)
        {
            // These are the existing local threshold crossing and recovery
            // events. Never serialize human-readable messages, paths or tokens.
            var state = item.Severity switch
            {
                "warning" => "severity=warning,event=raised",
                "info" => "severity=info,event=cleared",
                _ => null
            };
            if (state is null || item.SensorId.Length > 256 || item.TimestampMs <= 0) continue;
            sb.Append("sensordash_event,host=").Append(h)
              .Append(",sensor=").Append(TelemetryLineProtocol.Label(item.SensorId, 256))
              .Append(',').Append(state).Append(" value=1 ")
              .Append(item.TimestampMs.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }
}

internal sealed class TelemetryExportWorker(
    DashboardState state, HistoryStore database, ILogger<TelemetryExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var directory = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SensorDash");
        var settingsFile = Path.Combine(directory, "telemetry-export.json");
        var cursorFile = Path.Combine(directory, "telemetry-export-cursor.json");
        var metadataFile = Path.Combine(directory, "telemetry-export-metadata.json");
        var accessoryCursorFile = Path.Combine(directory, "telemetry-accessory-cursor.json");
        var eventCursorFile = Path.Combine(directory, "telemetry-event-cursor.json");
        TelemetryExportOptions options;
        try
        {
            options = TelemetryExportOptions.Load(settingsFile);
            if (!options.Enabled)
            {
                logger.LogInformation("Remote telemetry export disabled (opt-in configuration absent or disabled).");
                return;
            }
            options.Validate();
            var token = File.ReadAllText(options.TokenFile).Trim();
            if (token.Length < 40 || token.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Telemetry token must contain at least 40 hexadecimal characters.");
            using var ca = X509CertificateLoader.LoadCertificateFromFile(options.CaCertificateFile);
            // Only migrate/index the archive when the opt-in receiver credentials
            // are present and valid; never burden fan-control startup otherwise.
            database.EnsureTelemetryIndex();
            using var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert == null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable |
                    SslPolicyErrors.RemoteCertificateNameMismatch)) != 0) return false;
                using var verifier = new X509Chain();
                verifier.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                verifier.ChainPolicy.CustomTrustStore.Add(ca);
                verifier.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return verifier.Build(cert);
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            var host = string.IsNullOrWhiteSpace(options.HostLabel)
                ? Environment.MachineName : options.HostLabel;
            var endpoint = new Uri(options.Endpoint);
            // Start at the latest already-archived sample. Historical migration is
            // a separate, explicit operation; no surprise 30-day backfill.
            TelemetryCursor cursor;
            if (File.Exists(cursorFile))
            {
                cursor = JsonSerializer.Deserialize<TelemetryCursor>(
                    File.ReadAllText(cursorFile), JsonSerializerOptions.Web)
                    ?? throw new InvalidDataException("Invalid telemetry cursor.");
            }
            else
            {
                cursor = new TelemetryCursor(database.LatestTelemetryTimestamp(), "\uffff");
                SaveCursor(cursorFile, cursor);
            }
            // A second cursor tracks derived accessory watts independently.
            // On first activation there is no surprise historical backfill.
            var accessoryCursor = File.Exists(accessoryCursorFile)
                ? JsonSerializer.Deserialize<AccessoryCursor>(
                    File.ReadAllText(accessoryCursorFile), JsonSerializerOptions.Web)
                    ?? throw new InvalidDataException("Invalid accessory telemetry cursor.")
                : new AccessoryCursor(database.LatestAccessoryPowerTimestamp(), "\uffff", "\uffff");
            if (!File.Exists(accessoryCursorFile))
                SaveAccessoryCursor(accessoryCursorFile, accessoryCursor);
            // The last server-acknowledged catalog is kept separate from the
            // data cursor: edits to aliases or cooling mappings may remove
            // previously published tag sets, which require explicit 0 tombstones.
            var eventCursor = File.Exists(eventCursorFile)
                ? JsonSerializer.Deserialize<long>(File.ReadAllText(eventCursorFile), JsonSerializerOptions.Web)
                : 0L;
            long lastSpoolAt = 0;
            var lastMetadata = TelemetryMetadata.Load(metadataFile);
            long lastMetadataAt = 0;
            long lastMetadataCheckAt = 0;
            logger.LogInformation("Telemetry export enabled; cursor starts at {TimestampMs}.", cursor.TimestampMs);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var latest = state.Latest;
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    // A large retry backlog can flush many batches per second.
                    // Only rebuild the potentially large catalog every 5 seconds.
                    if (latest is { Error: null } && latest.Sensors.Count > 0 &&
                        now - lastMetadataCheckAt >= 5_000)
                    {
                        lastMetadataCheckAt = now;
                        try
                        {
                            var current = TelemetryMetadata.Build(latest, state.Settings, host);
                            if (!current.SetEquals(lastMetadata) || now - lastMetadataAt >= 120_000)
                            {
                                var metadataPayload = TelemetryMetadata.Serialize(current, lastMetadata, now);
                                foreach (var chunk in TelemetryMetadata.Chunks(metadataPayload))
                                {
                                    using var metadataRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
                                    metadataRequest.Headers.Authorization =
                                        new AuthenticationHeaderValue("Bearer", token);
                                    metadataRequest.Content = new StringContent(chunk, Encoding.UTF8, "text/plain");
                                    using var metadataResponse = await client.SendAsync(metadataRequest, stop);
                                    metadataResponse.EnsureSuccessStatusCode();
                                }
                                // Only persist the new snapshot after the entire
                                // batch was acknowledged; replays are idempotent.
                                TelemetryMetadata.Save(metadataFile, current);
                                lastMetadata = current;
                                lastMetadataAt = now;
                            }
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                        catch (Exception e)
                        {
                            // Catalog metadata must never block numeric readings
                            // or the local hardware control path.
                            logger.LogWarning(e, "Telemetry catalog synchronization failed; retrying.");
                        }
                    }
                    var rows = database.ReadTelemetryBatch(cursor);
                    if (rows.Count > 0)
                    {
                        var types = (state.Latest?.Sensors ?? [])
                            .ToDictionary(s => s.Id, s => s.Type, StringComparer.Ordinal);
                        var payload = TelemetryLineProtocol.Serialize(rows, host, types);
                        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
                        using var response = await client.SendAsync(request, stop);
                        response.EnsureSuccessStatusCode();
                        var last = rows[^1];
                        cursor = new TelemetryCursor(last.TimestampMs, last.SensorId);
                        SaveCursor(cursorFile, cursor);
                    }
                    // Archive-time estimates are never recalculated from the
                    // current calibration during retries or sleep resumes.
                    var power = database.ReadAccessoryPowerBatch(
                        accessoryCursor.TimestampMs, accessoryCursor.System, accessoryCursor.Component);
                    if (power.Count > 0)
                    {
                        using var powerRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
                        powerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        powerRequest.Content = new StringContent(
                            AccessoryPowerExportProtocol.Serialize(power, host), Encoding.UTF8, "text/plain");
                        using var powerResponse = await client.SendAsync(powerRequest, stop);
                        powerResponse.EnsureSuccessStatusCode();
                        var last = power[^1];
                        accessoryCursor = new AccessoryCursor(last.TimestampMs, last.System, last.Component);
                        SaveAccessoryCursor(accessoryCursorFile, accessoryCursor);
                    }
                    // Persist events with their original event times. The
                    // initial 0 cursor imports only events still held by the
                    // bounded local Windows SQLite archive.
                    var events = database.ReadThresholdEvents(eventCursor);
                    if (events.Count > 0)
                    {
                        var payload = ThresholdEventProtocol.Serialize(events, host);
                        if (payload.Length > 0)
                        {
                            using var eventRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
                            eventRequest.Headers.Authorization =
                                new AuthenticationHeaderValue("Bearer", token);
                            eventRequest.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
                            using var eventResponse = await client.SendAsync(eventRequest, stop);
                            eventResponse.EnsureSuccessStatusCode();
                        }
                        eventCursor = events[^1].Id;
                        SaveEventCursor(eventCursorFile, eventCursor);
                    }
                    // The archive remains a minimal durable retry buffer. Send
                    // three tiny aggregate gauges periodically, never filenames
                    // or per-sensor disk allocation estimates.
                    if (now - lastSpoolAt >= 60_000)
                    {
                        var spool = database.StorageFootprint(host, now);
                        using var spoolRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
                        spoolRequest.Headers.Authorization =
                            new AuthenticationHeaderValue("Bearer", token);
                        spoolRequest.Content = new StringContent(spool, Encoding.UTF8, "text/plain");
                        using var spoolResponse = await client.SendAsync(spoolRequest, stop);
                        spoolResponse.EnsureSuccessStatusCode();
                        lastSpoolAt = now;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(
                        rows.Count >= 384 || power.Count >= 256 ? 500 :
                        rows.Count > 0 || power.Count > 0 ? 3000 : 7000), stop);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Telemetry delivery failed; keeping local history and retrying.");
                    try { await Task.Delay(TimeSpan.FromSeconds(15), stop); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Telemetry exporter could not initialize; local control and history remain available.");
        }
    }

    internal static void SaveEventCursor(string path, long cursor)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cursor, JsonSerializerOptions.Web));
        File.Move(temp, path, true);
    }

    internal static void SaveAccessoryCursor(string path, AccessoryCursor cursor)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cursor, JsonSerializerOptions.Web));
        File.Move(tmp, path, true);
    }

    internal static void SaveCursor(string path, TelemetryCursor cursor)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(cursor, JsonSerializerOptions.Web));
        File.Move(temp, path, true);
    }
}

internal static class TelemetryExportSelfTest
{
    public static void Run()
    {
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Telemetry self-test: " + message);
        }
        var config = new TelemetryExportOptions { Enabled = true, Endpoint = "https://192.168.1.10:8429/ingest",
            TokenFile = @"C:\test\token", CaCertificateFile = @"C:\test\ca.crt" };
        config.Validate();
        var bad = new TelemetryExportOptions { Endpoint = "http://192.168.1.10:8429/ingest",
            TokenFile = config.TokenFile, CaCertificateFile = config.CaCertificateFile };
        try { bad.Validate(); throw new InvalidOperationException("Accepted insecure endpoint."); }
        catch (InvalidDataException) { }
        Require(TelemetryLineProtocol.Label("core sensor,=\n", 30) == "core_sensor___",
            "unsafe label characters");
        var lines = TelemetryLineProtocol.Serialize(
            [new TelemetryRow("/cpu/0/temperature/0", 1793450400000, 54.25)],
            "WIN-PC", new Dictionary<string, string>{{"/cpu/0/temperature/0", "Temperature"}});
        Require(lines.Contains("sensor=/cpu/0/temperature/0,kind=Temperature value=54.25 1793450400000"),
            "line formatting or timestamps");
        Require(!lines.Contains("NaN"), "invalid metric");
        var formattedEvent = ThresholdEventProtocol.Serialize(
            [new ThresholdTelemetryEvent(1, 1793450400000, "/amdcpu/0/temperature/0", "warning"),
             new ThresholdTelemetryEvent(2, 1793450410000, "/amdcpu/0/temperature/0", "info")], "main-desktop");
        Require(formattedEvent.Contains("severity=warning,event=raised value=1 1793450400000"),
            "event warning encoding");
        Require(formattedEvent.Contains("severity=info,event=cleared value=1 1793450410000"),
            "event recovery encoding");
        Require(!formattedEvent.Contains("message="), "free-form event data must stay local");
        var root = Path.Combine(Path.GetTempPath(), "SensorDash-telemetry-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var history = new HistoryStore(Path.Combine(root, "telemetry.db"));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "telemetry.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO samples(sensor,ts,value) VALUES
                    ('a',1793450400000,0),
                    ('b',1793450400000,30),
                    ('c',1793450410000,40)
                    """;
                command.ExecuteNonQuery();
            }
            history.Event("warning", "/amdcpu/0/temperature/0", "CPU warning", 81);
            history.Event("info", "/amdcpu/0/temperature/0", "CPU clear", 55);
            var firstEvent = history.ReadThresholdEvents(0, 1);
            Require(firstEvent.Count == 1 && firstEvent[0].Severity == "warning",
                "event cursor initial pagination");
            Require(history.ReadThresholdEvents(firstEvent[0].Id).Count == 1,
                "event cursor retained second row");
            var spool = history.StorageFootprint("WIN-PC", 1793450400000);
            Require(spool.Contains("file=database value=") && spool.Contains("file=wal value=")
                && !spool.Contains("telemetry.db"), "safe local spool gauges");
            history.EnsureTelemetryIndex();
            var one = history.ReadTelemetryBatch(new TelemetryCursor(0, ""), 1);
            Require(one.Count == 1 && one[0].SensorId == "a" && one[0].Value == 0,
                "first row / zero preservation");
            var rest = history.ReadTelemetryBatch(new TelemetryCursor(one[0].TimestampMs, one[0].SensorId));
            Require(rest.Count == 2 && rest[0].SensorId == "b" && rest[1].SensorId == "c",
                "same timestamp cursor pagination");
            var cursorPath = Path.Combine(root, "cursor.json");
            TelemetryExportWorker.SaveCursor(cursorPath, new TelemetryCursor(rest[^1].TimestampMs, rest[^1].SensorId));
            var reloaded = JsonSerializer.Deserialize<TelemetryCursor>(File.ReadAllText(cursorPath),
                JsonSerializerOptions.Web);
            Require(reloaded?.SensorId == "c", "cursor persistence");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
