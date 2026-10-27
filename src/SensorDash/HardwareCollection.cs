using LibreHardwareMonitor.Hardware;

// LibreHardwareMonitor enumerates these hardware *families*, not individually
// independently addressable register reads. Collection can be disabled at the
// family level before Computer.Open(), or at the discovered device update level.
public sealed record HardwareCollectionGroup(string Id, string Name, string Description);

public static class HardwareCollection
{
    public static readonly HardwareCollectionGroup[] Groups =
    [
        new("Cpu", "Processors", "CPU package and core sensors"),
        new("Gpu", "Graphics adapters", "GPU temperatures, fans, clocks and power"),
        new("Motherboard", "Motherboard and Super I/O", "Board sensors and associated controllers"),
        new("Memory", "Memory", "RAM hardware telemetry"),
        new("Storage", "Storage", "SSDs, hard disks and drive SMART telemetry"),
        new("Network", "Network adapters", "Interface throughput and utilization"),
        new("Controller", "Cooling / auxiliary controllers", "Supported external hardware controllers"),
        new("PowerMonitor", "Power monitors", "Supported independent power-monitoring devices")
    ];

    private static readonly HashSet<string> KnownIds =
        Groups.Select(g=>g.Id).ToHashSet(StringComparer.Ordinal);

    public static List<string> NormalizeGroups(List<string>? groups) =>
        (groups ?? []).Where(KnownIds.Contains).Distinct(StringComparer.Ordinal).ToList();

    public static bool Enabled(DashboardSettings settings, string group) =>
        !(settings.DisabledCollectionGroups ?? []).Contains(group, StringComparer.Ordinal);

    public static string Signature(DashboardSettings settings) =>
        string.Join("|", Groups.Select(g=>Enabled(settings,g.Id) ? "1" : "0"));

    public static Computer CreateComputer(DashboardSettings settings) => new()
    {
        IsCpuEnabled = Enabled(settings,"Cpu"),
        IsGpuEnabled = Enabled(settings,"Gpu"),
        IsMotherboardEnabled = Enabled(settings,"Motherboard"),
        IsMemoryEnabled = Enabled(settings,"Memory"),
        IsStorageEnabled = Enabled(settings,"Storage"),
        IsNetworkEnabled = Enabled(settings,"Network"),
        IsControllerEnabled = Enabled(settings,"Controller"),
        IsPowerMonitorEnabled = Enabled(settings,"PowerMonitor")
    };

    // This is the one unavoidable adapter between LHM's device types and
    // collection-family switches. Unknown future device types are not silently
    // classified as network or motherboard.
    public static string? FamilyForDeviceType(string type) => type switch
    {
        "Cpu" => "Cpu",
        "GpuAmd" or "GpuNvidia" or "GpuIntel" => "Gpu",
        "Motherboard" or "SuperIO" => "Motherboard",
        "Memory" => "Memory",
        "Storage" => "Storage",
        "Network" => "Network",
        "Controller" or "EmbeddedController" or "Cooler" => "Controller",
        "PowerMonitor" => "PowerMonitor",
        _ => null
    };
}
