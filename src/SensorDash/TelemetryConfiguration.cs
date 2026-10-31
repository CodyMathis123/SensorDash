using System.Text.Json;
using System.Text.RegularExpressions;

// A display-safe configuration surface. Collector stores credentials on disk
// and does not send tokens or certificate bytes back to the desktop web UI.
public sealed record TelemetryConfigurationUpdate(bool Enabled, string Endpoint, string HostLabel);

internal sealed class TelemetryConfigurationStatus
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "";
    public string HostLabel { get; init; } = "";
    public bool TokenReady { get; init; }
    public bool CaReady { get; init; }
    public bool RestartRequired { get; init; }
    public bool Configured { get; init; }
    public string Note { get; init; } = "Export status is configuration-only; confirm samples in Grafana.";
}

internal static class TelemetryConfiguration
{
    private static readonly object Gate = new();
    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorDash");

    public static TelemetryConfigurationStatus Snapshot() => Snapshot(DataDirectory);

    internal static TelemetryConfigurationStatus Snapshot(string directory)
    {
        lock (Gate)
        {
            string path = Path.Combine(directory, "telemetry-export.json");
            var options = TelemetryExportOptions.Load(path);
            string tokenFile = string.IsNullOrWhiteSpace(options.TokenFile)
                ? Path.Combine(directory, "telemetry-token.txt") : options.TokenFile;
            string caFile = string.IsNullOrWhiteSpace(options.CaCertificateFile)
                ? Path.Combine(directory, "telemetry-ca.crt") : options.CaCertificateFile;
            bool changedAfterStartup = File.Exists(path) &&
                File.GetLastWriteTimeUtc(path) > System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            return new TelemetryConfigurationStatus
            {
                Configured = File.Exists(path),
                Enabled = options.Enabled,
                Endpoint = options.Endpoint,
                HostLabel = options.HostLabel,
                TokenReady = File.Exists(tokenFile),
                CaReady = File.Exists(caFile),
                RestartRequired = changedAfterStartup,
            };
        }
    }

    public static TelemetryConfigurationStatus Save(TelemetryConfigurationUpdate requested) => Save(requested, DataDirectory);

    internal static TelemetryConfigurationStatus Save(TelemetryConfigurationUpdate requested, string directory)
    {
        if (requested is null) throw new ArgumentNullException(nameof(requested));
        var endpoint = (requested.Endpoint ?? "").Trim();
        var host = (requested.HostLabel ?? "").Trim();
        if (endpoint.Length > 256 || host.Length is < 1 or > 80 ||
            !Regex.IsMatch(host, @"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Use a short host label containing letters, numbers, dots, dashes or underscores.");
        if (endpoint.Length == 0 && requested.Enabled)
            throw new InvalidDataException("An HTTPS collector endpoint is required to enable export.");
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "telemetry-export.json");
            var existing = TelemetryExportOptions.Load(path);
            existing.Enabled = requested.Enabled;
            existing.Endpoint = endpoint;
            existing.HostLabel = host;
            // Preserve existing machine-local credential locations. Never accept
            // token bytes, CA material, or file paths from an HTTP request.
            if (string.IsNullOrWhiteSpace(existing.TokenFile))
                existing.TokenFile = Path.Combine(directory, "telemetry-token.txt");
            if (string.IsNullOrWhiteSpace(existing.CaCertificateFile))
                existing.CaCertificateFile = Path.Combine(directory, "telemetry-ca.crt");
            if (endpoint.Length > 0) existing.Validate();
            if (existing.Enabled && (!File.Exists(existing.TokenFile) || !File.Exists(existing.CaCertificateFile)))
                throw new InvalidDataException("Install the Pi-issued telemetry token and CA certificate before enabling export.");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(existing,
                new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return Snapshot(directory);
        }
    }
}

internal static class TelemetryConfigurationSelfTest
{
    public static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "SensorDash-config-selftest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var initially = TelemetryConfiguration.Snapshot(dir);
            if (initially.Enabled || initially.TokenReady || initially.CaReady || initially.Configured)
                throw new InvalidOperationException("Telemetry config: unsafe initial state.");
            var candidate = new TelemetryConfigurationUpdate(false, "https://192.168.1.43:8429/ingest", "main-desktop");
            var saved = TelemetryConfiguration.Save(candidate, dir);
            if (saved.Enabled || !saved.Configured || saved.HostLabel != "main-desktop")
                throw new InvalidOperationException("Telemetry config: disabled draft failed.");
            var serialized = File.ReadAllText(Path.Combine(dir, "telemetry-export.json"));
            if (!serialized.Contains("telemetry-token.txt") || !serialized.Contains("telemetry-ca.crt"))
                throw new InvalidOperationException("Telemetry config: credential paths missing.");
            foreach (var invalid in new[] {
                new TelemetryConfigurationUpdate(true, "http://example.com/ingest", "pc"),
                new TelemetryConfigurationUpdate(false, "https://example.com/ingest", "invalid label"),
                new TelemetryConfigurationUpdate(true, "", "pc"),
            })
            {
                bool refused = false;
                try { TelemetryConfiguration.Save(invalid, dir); }
                catch (InvalidDataException) { refused = true; }
                if (!refused) throw new InvalidOperationException("Telemetry config: invalid change accepted.");
            }
            try { TelemetryConfiguration.Save(candidate with { Enabled = true }, dir);
                throw new InvalidOperationException("Telemetry config: accepted missing credential files.");
            } catch (InvalidDataException) { }
            File.WriteAllText(Path.Combine(dir, "telemetry-token.txt"), "test-token-placeholder");
            File.WriteAllText(Path.Combine(dir, "telemetry-ca.crt"), "test-cert-placeholder");
            var active = TelemetryConfiguration.Save(candidate with { Enabled = true }, dir);
            if (!active.Enabled || !active.CaReady || !active.TokenReady)
                throw new InvalidOperationException("Telemetry config: readiness reporting failed.");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
