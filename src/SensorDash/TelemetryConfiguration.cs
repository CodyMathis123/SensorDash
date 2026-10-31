using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

// A display-safe configuration surface. Collector stores credentials on disk
// and does not send tokens or certificate bytes back to the desktop web UI.
public sealed record TelemetryConfigurationUpdate(bool Enabled, string Endpoint, string HostLabel, string? Token = null, string? CaCertificatePem = null);

internal sealed class TelemetryConfigurationStatus
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "";
    public string HostLabel { get; init; } = "";
    public bool TokenReady { get; init; }
    public bool CaReady { get; init; }
    // The exporter reopens its own HTTPS session when settings change. It does
    // not restart the Windows hardware-control collector.
    public bool RestartRequired { get; init; }
    public string RuntimeState { get; init; } = "disabled";
    public DateTimeOffset? LastDeliveredUtc { get; init; }
    public string LastFailureClass { get; init; } = "";
    public int ReloadCount { get; init; }
    public bool Configured { get; init; }
    public string Note { get; init; } = "Export status is configuration-only; confirm samples in Grafana.";
}

internal sealed record ExportRuntimeSnapshot(
    string State, DateTimeOffset? LastDeliveredUtc, string LastFailureClass, int ReloadCount);

// Reporting is strictly observational. These fields contain no token, CA,
// URL, sensor identity, event message or fan-control command.
internal static class TelemetryExportRuntime
{
    private static readonly object Gate = new();
    private static string _state = "disabled";
    private static DateTimeOffset? _lastDelivered;
    private static string _lastFailure = "";
    private static int _reloads;

    internal static ExportRuntimeSnapshot Snapshot()
    {
        lock (Gate) return new(_state, _lastDelivered, _lastFailure, _reloads);
    }
    internal static void Disabled()
    {
        lock (Gate) { _state = "disabled"; _lastFailure = ""; }
    }
    internal static void Running()
    {
        lock (Gate) { _state = "running"; _lastFailure = ""; }
    }
    internal static void Delivered()
    {
        lock (Gate) { _lastDelivered = DateTimeOffset.UtcNow; _state = "running"; _lastFailure = ""; }
    }
    internal static void Failed(Exception e)
    {
        lock (Gate) { _state = "retrying"; _lastFailure = e.GetType().Name; }
    }
    internal static void Reloading()
    {
        lock (Gate) { _state = "reloading"; _lastFailure = ""; _reloads++; }
    }

    internal static string Fingerprint(params string[] paths)
    {
        // Check contents, not only NTFS write timestamps: an atomic file
        // replacement or same-size token rotation must always be detected.
        // Never log the fingerprint or secret contents.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var path in paths)
        {
            hash.AppendData(File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>());
            hash.AppendData(new byte[] { 0 });
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal static class TelemetryConfiguration
{
    private static readonly object Gate = new();
    private static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SensorDash");

    // A newly created secret is empty until its NTFS DACL is set. Write
    // credentials only after granting access solely to SYSTEM/Admins, then
    // atomically rename the protected file. Never leave plaintext in a
    // temporary file inheriting ProgramData's possibly broad folder ACL.
    private static void WriteProtectedCredential(string path, string contents)
    {
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Create the *empty* temporary file first, release its exclusive
        // handle, and set the DACL before writing any secret bytes. Windows
        // ACL APIs may open a second handle, which FileShare.None would block.
        using (var empty = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { }
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(temp).SetAccessControl(acl);
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var bytes = Encoding.UTF8.GetBytes(contents);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

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
            var runtime = TelemetryExportRuntime.Snapshot();
            return new TelemetryConfigurationStatus
            {
                Configured = File.Exists(path),
                Enabled = options.Enabled,
                Endpoint = options.Endpoint,
                HostLabel = options.HostLabel,
                TokenReady = File.Exists(tokenFile),
                CaReady = File.Exists(caFile),
                RestartRequired = false,
                RuntimeState = runtime.State,
                LastDeliveredUtc = runtime.LastDeliveredUtc,
                LastFailureClass = runtime.LastFailureClass,
                ReloadCount = runtime.ReloadCount,
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
        // Raw credentials are accepted only on explicit save, over the local
        // same-origin management endpoint. GET never returns their bytes.
        var newToken = (requested.Token ?? "").Trim();
        var newCa = (requested.CaCertificatePem ?? "").Trim();
        if (newToken.Length > 0 && !Regex.IsMatch(newToken, @"^[A-Fa-f0-9]{40,128}$"))
            throw new InvalidDataException("Telemetry bearer must contain 40–128 hexadecimal characters.");
        if (newCa.Length > 0)
        {
            if (newCa.Length > 16_384 || !newCa.StartsWith("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ||
                !newCa.EndsWith("-----END CERTIFICATE-----", StringComparison.Ordinal))
                throw new InvalidDataException("Paste a PEM-encoded CA certificate (not a private key).");
            try
            {
                using var candidate = X509Certificate2.CreateFromPem(newCa);
                var caConstraints = candidate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
                if (caConstraints?.CertificateAuthority != true)
                    throw new InvalidDataException("The certificate must be a CA certificate.");
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                throw new InvalidDataException("The CA certificate could not be parsed.");
            }
        }
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
            if (newToken.Length > 0)
            {
                WriteProtectedCredential(existing.TokenFile, newToken + Environment.NewLine);
            }
            if (newCa.Length > 0)
            {
                WriteProtectedCredential(existing.CaCertificateFile, newCa + Environment.NewLine);
            }
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
            var actualSecret = "a".PadRight(64, 'a');
            var withToken = TelemetryConfiguration.Save(candidate with { Token = actualSecret }, dir);
            if (withToken.Enabled || !withToken.TokenReady ||
                JsonSerializer.Serialize(withToken, JsonSerializerOptions.Web).Contains(actualSecret, StringComparison.Ordinal))
                throw new InvalidOperationException("Telemetry config: secret leaked or missing.");
            // Secret files must not inherit broad ProgramData/Temp ACLs.
            var tokenAcl = new FileInfo(Path.Combine(dir, "telemetry-token.txt")).GetAccessControl();
            if (!tokenAcl.AreAccessRulesProtected ||
                tokenAcl.GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Any(rule =>
                        rule.AccessControlType == AccessControlType.Allow &&
                        rule.IdentityReference is SecurityIdentifier sid &&
                        sid != new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) &&
                        sid != new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
                throw new InvalidOperationException("Telemetry config: credential ACL is not restricted.");
            var active = TelemetryConfiguration.Save(candidate with { Enabled = true }, dir);
            if (active.RestartRequired) throw new InvalidOperationException("Telemetry export must hot-reload.");
            var oldFingerprint = TelemetryExportRuntime.Fingerprint(
                Path.Combine(dir, "telemetry-export.json"),
                Path.Combine(dir, "telemetry-token.txt"));
            File.WriteAllText(Path.Combine(dir, "telemetry-token.txt"), new string('b', 64));
            var freshFingerprint = TelemetryExportRuntime.Fingerprint(
                Path.Combine(dir, "telemetry-export.json"),
                Path.Combine(dir, "telemetry-token.txt"));
            if (oldFingerprint == freshFingerprint)
                throw new InvalidOperationException("Token rotation must trigger export reload.");
            if (!active.Enabled || !active.CaReady || !active.TokenReady)
                throw new InvalidOperationException("Telemetry config: readiness reporting failed.");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
