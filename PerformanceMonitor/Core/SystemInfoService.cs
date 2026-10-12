using System.Management;
using Microsoft.Win32;

namespace PerformanceMonitor.Core;

public sealed class GpuInfo
{
    public string Name { get; init; } = "";
    public string Driver { get; init; } = "";
    public string Resolution { get; init; } = "";
}

public sealed class DriveRow
{
    public string Letter { get; init; } = "";
    public string Label { get; init; } = "";
    public string Type { get; init; } = "";
    public long Total { get; init; }
    public long Free { get; init; }
    public bool IsSystem { get; init; }
    public double UsedPercent => Total == 0 ? 0 : (Total - Free) * 100.0 / Total;
    public string UsageText => $"{Fmt.Bytes(Free)} frei von {Fmt.Bytes(Total)}  ·  {UsedPercent:0} % belegt";
}

public sealed class SystemSummary
{
    public string CpuName { get; set; } = "Unbekannter Prozessor";
    public int Cores { get; set; }
    public int Threads { get; set; } = Environment.ProcessorCount;
    public int MaxClockMhz { get; set; }
    public List<GpuInfo> Gpus { get; } = new();
    public ulong RamTotal { get; set; }
    public string RamDetails { get; set; } = "";
    public string OsName { get; set; } = "Windows";
    public string OsVersion { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Board { get; set; } = "";
    public string Bios { get; set; } = "";
    public bool IsLaptop { get; set; }

    /// <summary>Dedizierte Grafikkarte (NVIDIA, AMD Radeon RX/Pro, Intel Arc) zusätzlich zur integrierten Grafik.</summary>
    public bool HasDedicatedGpu => Gpus.Any(g =>
        g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
        g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
        g.Name.Contains("Arc A", StringComparison.OrdinalIgnoreCase) || g.Name.Contains("Arc B", StringComparison.OrdinalIgnoreCase));

    public string GpuShort => Gpus.Count == 0 ? "Keine Grafikkarte erkannt" : string.Join(" · ", Gpus.Select(g => g.Name));
}

public static class SystemInfoService
{
    private static Task<SystemSummary>? _summary;

    public static Task<SystemSummary> GetSummaryAsync() => _summary ??= Task.Run(LoadSummary);

    private static IEnumerable<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        var results = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            foreach (var o in searcher.Get()) results.Add(o);
        }
        catch { }
        return results;
    }

    private static SystemSummary LoadSummary()
    {
        var s = new SystemSummary();

        foreach (var cpu in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            s.CpuName = (cpu["Name"]?.ToString() ?? s.CpuName).Trim();
            s.Cores += Convert.ToInt32(cpu["NumberOfCores"] ?? 0);
            s.Threads = Convert.ToInt32(cpu["NumberOfLogicalProcessors"] ?? s.Threads);
            s.MaxClockMhz = Convert.ToInt32(cpu["MaxClockSpeed"] ?? 0);
        }
        if (s.CpuName.StartsWith("Unbekannt"))
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string n) s.CpuName = n.Trim();
        }

        foreach (var gpu in Query("SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController"))
        {
            var name = gpu["Name"]?.ToString() ?? "";
            if (name.Length == 0 || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
            var res = gpu["CurrentHorizontalResolution"] != null
                ? $"{gpu["CurrentHorizontalResolution"]} × {gpu["CurrentVerticalResolution"]} @ {gpu["CurrentRefreshRate"]} Hz"
                : "";
            s.Gpus.Add(new GpuInfo { Name = name.Trim(), Driver = gpu["DriverVersion"]?.ToString() ?? "", Resolution = res });
        }

        var mem = Native.GetMemoryStatus();
        s.RamTotal = mem.ullTotalPhys;
        var modules = Query("SELECT Capacity, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory").ToList();
        if (modules.Count > 0)
        {
            ulong cap = Convert.ToUInt64(modules[0]["Capacity"] ?? 0UL);
            uint speed = Convert.ToUInt32(modules[0]["ConfiguredClockSpeed"] ?? modules[0]["Speed"] ?? 0u);
            var type = Convert.ToUInt32(modules[0]["SMBIOSMemoryType"] ?? 0u) switch
            {
                24 => "DDR3", 26 => "DDR4", 34 => "DDR5", 30 => "LPDDR4", 35 => "LPDDR5", _ => "",
            };
            bool same = modules.All(m => Convert.ToUInt64(m["Capacity"] ?? 0UL) == cap);
            var layout = same ? $"{modules.Count} × {Fmt.Bytes(cap)}" : $"{modules.Count} Module";
            s.RamDetails = string.Join(" · ", new[] { layout, type, speed > 0 ? $"{speed} MT/s" : "" }.Where(x => x.Length > 0));
        }

        foreach (var os in Query("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem"))
        {
            s.OsName = os["Caption"]?.ToString()?.Replace("Microsoft ", "") ?? s.OsName;
            s.OsVersion = $"{os["Version"]}";
        }
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = key?.GetValue("DisplayVersion")?.ToString();
            var ubr = key?.GetValue("UBR")?.ToString();
            if (!string.IsNullOrEmpty(display)) s.OsVersion = $"{display} (Build {key?.GetValue("CurrentBuild")}.{ubr})";
        }
        catch { }

        foreach (var cs in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            s.Manufacturer = cs["Manufacturer"]?.ToString()?.Trim() ?? "";
            s.Model = cs["Model"]?.ToString()?.Trim() ?? "";
        }
        // Gehäusetyp: Laptop, Notebook, Convertible, Tablet …
        int[] portable = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
        foreach (var enc in Query("SELECT ChassisTypes FROM Win32_SystemEnclosure"))
            if (enc["ChassisTypes"] is ushort[] types && types.Any(t => portable.Contains(t))) s.IsLaptop = true;
        if (!s.IsLaptop && Query("SELECT Name FROM Win32_Battery").Any()) s.IsLaptop = true;

        foreach (var bb in Query("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            s.Board = $"{bb["Manufacturer"]} {bb["Product"]}".Trim();
        foreach (var bios in Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            s.Bios = bios["SMBIOSBIOSVersion"]?.ToString() ?? "";

        return s;
    }

    /// <summary>Ermittelt pro Laufwerksbuchstabe den Typ des physischen Datenträgers (SSD/HDD/NVMe).</summary>
    public static Dictionary<char, string> GetDiskTypes()
    {
        var result = new Dictionary<char, string>();
        const string scope = @"root\Microsoft\Windows\Storage";
        var diskTypes = new Dictionary<string, string>();
        foreach (var pd in Query("SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk", scope))
        {
            var id = pd["DeviceId"]?.ToString() ?? "";
            int media = Convert.ToInt32(pd["MediaType"] ?? 0);
            int bus = Convert.ToInt32(pd["BusType"] ?? 0);
            diskTypes[id] = media switch
            {
                3 => "HDD",
                4 => bus == 17 ? "NVMe-SSD" : "SSD",
                _ => bus == 17 ? "NVMe-SSD" : bus == 7 ? "USB" : "Unbekannt",
            };
        }
        foreach (var part in Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", scope))
        {
            char letter = part["DriveLetter"] switch
            {
                char c => c,
                ushort u => (char)u,
                _ => '\0',
            };
            if (letter == '\0') continue;
            var disk = part["DiskNumber"]?.ToString() ?? "";
            if (diskTypes.TryGetValue(disk, out var type)) result[char.ToUpperInvariant(letter)] = type;
        }
        return result;
    }

    public static List<DriveRow> GetDrives(Dictionary<char, string>? types = null)
    {
        types ??= GetDiskTypes();
        var systemRoot = char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)?[0] ?? 'C');
        var list = new List<DriveRow>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                char letter = char.ToUpperInvariant(d.Name[0]);
                list.Add(new DriveRow
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Lokaler Datenträger" : d.VolumeLabel,
                    Type = types.GetValueOrDefault(letter, d.DriveType == DriveType.Removable ? "Wechseldatenträger" : "Unbekannt"),
                    Total = d.TotalSize,
                    Free = d.AvailableFreeSpace,
                    IsSystem = letter == systemRoot,
                });
            }
            catch { }
        }
        return list;
    }

    /// <summary>ACPI-Temperatur (nur mit Adminrechten und nicht auf allen Geräten verfügbar).</summary>
    public static double? TryGetThermalCelsius()
    {
        double? max = null;
        foreach (var tz in Query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature", @"root\WMI"))
        {
            double c = Convert.ToDouble(tz["CurrentTemperature"] ?? 0) / 10.0 - 273.15;
            if (c is > 15 and < 115) max = Math.Max(max ?? 0, c);
        }
        return max;
    }
}
