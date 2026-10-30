using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

public sealed record PawnIoStatus(string State, string? Version, bool NeedsAttention, string Message);
internal sealed record PawnIoProbe(string? Version, bool DeviceAccessible, string? Error = null);

// Probe from the elevated collector, never the standard-user browser host.
// Opening/closing a handle tests access only: no modules, IOCTLs or writes.
internal sealed class PawnIoMonitor
{
    private DateTimeOffset _checked = DateTimeOffset.MinValue;
    private PawnIoProbe? _initial;
    private PawnIoProbe? _current;
    internal PawnIoStatus Read(DashboardSettings settings)
    {
        if (_current is null || DateTimeOffset.UtcNow - _checked >= TimeSpan.FromSeconds(30))
        {
            _current = Probe(); _initial ??= _current; _checked = DateTimeOffset.UtcNow;
        }
        bool relevant = new[] { "Cpu", "Motherboard", "Memory" }.Any(g => HardwareCollection.Enabled(settings, g));
        return Describe(_current, _initial, relevant);
    }

    internal static PawnIoStatus Describe(PawnIoProbe current, PawnIoProbe initial, bool relevant)
    {
        PawnIoStatus Status(string state, string message, bool warning = true) =>
            new(state, current.Version, warning && relevant, message);
        if (current.Error is not null)
            return Status("unknown", "Could not check PawnIO. Some hardware readings may be unavailable.");
        if (!Version.TryParse(current.Version, out var version))
            return Status("missing", "Install PawnIO to enable supported CPU and motherboard readings.");
        if (version.Major < 2)
            return Status("outdated", "Update PawnIO to version 2.0 or newer for supported CPU and motherboard readings.");
        if (!current.DeviceAccessible)
            return Status("unavailable", "PawnIO is installed but the collector cannot access it. Repair PawnIO or restart Windows.");
        if (initial.Version != current.Version || !initial.DeviceAccessible || initial.Error is not null)
            return Status("restart-required", "PawnIO is ready. Restart the SensorDash Collector service, or restart Windows, to rediscover sensors.");
        return Status("available", "Installed and accessible to the collector.", false);
    }

    private static PawnIoProbe Probe()
    {
        try
        {
            string? version = null;
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
                if (key?.GetValue("DisplayVersion") is string value && Version.TryParse(value, out _))
                { version = value; break; }
            }
            if (version is null) return new(null, false);
            using var handle = CreateFile(@"\\?\GLOBALROOT\Device\PawnIO", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            return new(version, !handle.IsInvalid);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or Win32Exception)
        { return new(null, false, ex.GetType().Name); }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    internal static void SelfTest()
    {
        void Check(PawnIoProbe p, string expected, PawnIoProbe? initial = null)
        {
            var status = Describe(p, initial ?? p, true);
            if (status.State != expected || status.NeedsAttention != (expected != "available"))
                throw new InvalidOperationException("PawnIO status regression: " + expected);
            if (Describe(p, initial ?? p, false).NeedsAttention)
                throw new InvalidOperationException("Disabled hardware must not require PawnIO.");
        }
        Check(new(null, false), "missing");
        Check(new("invalid", false), "missing");
        Check(new("1.2.0", true), "outdated");
        Check(new("2.0", true), "available");
        Check(new("2.2.0", false), "unavailable");
        Check(new(null, false, "AccessDenied"), "unknown");
        Check(new("2.2.0", true), "restart-required", new(null, false));
        Check(new("2.2.0", true), "restart-required", new("2.0", true));
        Check(new("2.2.0", true), "restart-required", new("2.2.0", false));
    }
}
