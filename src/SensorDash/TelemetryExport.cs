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

public sealed partial class HistoryStore
{
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
            WHERE ts > $ts OR (ts = $ts AND sensor > $sensor)
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

internal sealed class TelemetryExportWorker(
    DashboardState state, HistoryStore database, ILogger<TelemetryExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var directory = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData), "SensorDash");
        var settingsFile = Path.Combine(directory, "telemetry-export.json");
        var cursorFile = Path.Combine(directory, "telemetry-export-cursor.json");
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
            logger.LogInformation("Telemetry export enabled; cursor starts at {TimestampMs}.", cursor.TimestampMs);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var rows = database.ReadTelemetryBatch(cursor);
                    if (rows.Count == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(7), stop);
                        continue;
                    }
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
                    await Task.Delay(TimeSpan.FromMilliseconds(rows.Count >= 384 ? 500 : 3000), stop);
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
        Require(TelemetryLineProtocol.Label("core sensor,=\n", 30) == "core_sensor____",
            "unsafe label characters");
        var lines = TelemetryLineProtocol.Serialize(
            [new TelemetryRow("/cpu/0/temperature/0", 1793450400000, 54.25)],
            "WIN-PC", new Dictionary<string, string>{{"/cpu/0/temperature/0", "Temperature"}});
        Require(lines.Contains("sensor=/cpu/0/temperature/0,kind=Temperature value=54.25 1793450400000"),
            "line formatting or timestamps");
        Require(!lines.Contains("NaN"), "invalid metric");
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
