using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Desktop-owned preference. It is intentionally separate from collector settings:
// changing web exposure must never change or authorize hardware fan control.
public sealed class DashboardNetworkOptions
{
    public bool LanEnabled { get; set; }

    public static DashboardNetworkOptions Load(string directory)
    {
        try
        {
            var path = Path.Combine(directory, "network.json");
            return JsonSerializer.Deserialize<DashboardNetworkOptions>(
                File.ReadAllText(path), JsonSerializerOptions.Web) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public static void Save(string directory, bool lanEnabled)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "network.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new DashboardNetworkOptions
        {
            LanEnabled = lanEnabled
        }, new JsonSerializerOptions(JsonSerializerOptions.Web) { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }

    public static bool IsLocalClient(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    // Fail closed for non-private callers even if someone forwards TCP/8765.
    // Tailscale uses 100.64.0.0/10 and fd7a:115c:a1e0::/48; RFC6598 and
    // IPv6 ULA are accepted for private-network access, not public addresses.
    public static bool IsTrustedClient(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (bytes[0] & 0xfe) == 0xfc || // Unique-local addresses
                   (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80); // Link-local
        return false;
    }

    public static bool CanMutate(IPAddress? address) => IsLocalClient(address);
}

public static class DashboardNetworkOptionsSelfTest
{
    public static void Run()
    {
        static void Check(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Network policy: " + message);
        }
        Check(DashboardNetworkOptions.IsTrustedClient(IPAddress.Parse("127.0.0.1")), "IPv4 loopback");
        Check(DashboardNetworkOptions.CanMutate(IPAddress.Parse("::1")), "IPv6 loopback can write");
        Check(DashboardNetworkOptions.CanMutate(IPAddress.Parse("::ffff:127.0.0.1")), "mapped loopback can write");
        foreach (var address in new[] { "192.168.1.5", "10.0.0.2", "172.16.0.1",
            "172.31.255.254", "100.64.0.1", "100.127.255.254", "fd7a:115c:a1e0::1", "fe80::1" })
        {
            var ip = IPAddress.Parse(address);
            Check(DashboardNetworkOptions.IsTrustedClient(ip), "trusted " + address);
            Check(!DashboardNetworkOptions.CanMutate(ip), "remote read only " + address);
        }
        foreach (var address in new[] { "8.8.8.8", "172.32.0.1", "100.128.0.1",
            "169.254.1.1", "1.1.1.1", "2001:4860:4860::8888" })
            Check(!DashboardNetworkOptions.IsTrustedClient(IPAddress.Parse(address)), "public blocked " + address);
        Check(!DashboardNetworkOptions.IsTrustedClient(null) && !DashboardNetworkOptions.CanMutate(null), "null denied");
        var directory = Path.Combine(Path.GetTempPath(), "SensorDash-network-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            Check(!DashboardNetworkOptions.Load(directory).LanEnabled, "opt in required");
            DashboardNetworkOptions.Save(directory, true);
            Check(DashboardNetworkOptions.Load(directory).LanEnabled, "enabled persists");
            DashboardNetworkOptions.Save(directory, false);
            Check(!DashboardNetworkOptions.Load(directory).LanEnabled, "disabled persists");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
