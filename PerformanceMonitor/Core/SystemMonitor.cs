using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace PerformanceMonitor.Core;

public sealed class ProcessInfo
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    public string? Description { get; init; }
    public double Cpu { get; init; }
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    public double IoReadBps { get; init; }
    public double IoWriteBps { get; init; }
    public double Gpu { get; init; }
    public int Handles { get; init; }
    public ImageSource? Icon { get; init; }
    public double IoBps => IoReadBps + IoWriteBps;
}

/// <summary>ACPI-Thermalzone (z. B. \_TZ.CPUZ). PassiveLimit &lt; 100 bedeutet: das System drosselt wegen Hitze.</summary>
public sealed record ThermalZone(string Name, double Celsius, double? PassiveLimit, int ThrottleReasons);

/// <summary>Eine Messung des gesamten Systems zu einem Zeitpunkt.</summary>
public sealed class Snapshot
{
    public DateTime Time { get; init; }
    public double Cpu { get; init; }
    public double? CpuPerformance { get; init; }
    public double? CpuFrequencyMhz { get; init; }
    public double? Dpc { get; init; }
    public double? Interrupt { get; init; }
    public double? ProcessorQueue { get; init; }
    public ulong RamTotal { get; init; }
    public ulong RamUsed { get; init; }
    public ulong CommitLimit { get; init; }
    public ulong CommitUsed { get; init; }
    public double? HardFaults { get; init; }
    public double? DiskActive { get; init; }
    public double? DiskQueue { get; init; }
    public double DiskReadBps { get; init; }
    public double DiskWriteBps { get; init; }
    public double NetDownBps { get; init; }
    public double NetUpBps { get; init; }
    public double? Gpu { get; init; }
    public IReadOnlyList<ProcessInfo> Processes { get; init; } = [];
    public IReadOnlyList<ThermalZone> ThermalZones { get; init; } = [];

    public double RamPercent => RamTotal == 0 ? 0 : RamUsed * 100.0 / RamTotal;
    public double CommitPercent => CommitLimit == 0 ? 0 : CommitUsed * 100.0 / CommitLimit;
    public int HandleCount => Processes.Sum(p => p.Handles);
}

/// <summary>Sammelt periodisch Systemwerte im Hintergrund und meldet sie über <see cref="SnapshotReady"/>.</summary>
public sealed class SystemMonitor : IDisposable
{
    public event Action<Snapshot>? SnapshotReady;

    public Snapshot? Latest { get; private set; }
    public int IntervalMs { get; set; } = 1000;
    public bool GpuAvailable { get; private set; }

    private readonly CancellationTokenSource _cts = new();
    private readonly ProcessSampler _processes = new();
    private PdhQuery? _pdh;
    private IntPtr _cPerf, _cFreq, _cDpc, _cInterrupt, _cQueue, _cDiskIdle, _cDiskQueue, _cDiskRead, _cDiskWrite, _cPageReads, _cGpu, _cTzTemp, _cTzPassive, _cTzThrottle;
    private long _prevIdle, _prevKernel, _prevUser;
    private long _prevRx, _prevTx, _prevNetTicks;

    public void Start() => Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        try
        {
            Init();
            await Task.Delay(700, _cts.Token);
            while (!_cts.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var snapshot = Take();
                    Latest = snapshot;
                    SnapshotReady?.Invoke(snapshot);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex);
                }
                int wait = Math.Max(150, IntervalMs - (int)sw.ElapsedMilliseconds);
                await Task.Delay(wait, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _pdh?.Dispose();
        }
    }

    private void Init()
    {
        _pdh = new PdhQuery();
        _cPerf = _pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
        _cFreq = _pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
        _cDpc = _pdh.Add(@"\Processor(_Total)\% DPC Time");
        _cInterrupt = _pdh.Add(@"\Processor(_Total)\% Interrupt Time");
        _cQueue = _pdh.Add(@"\System\Processor Queue Length");
        _cDiskIdle = _pdh.Add(@"\PhysicalDisk(_Total)\% Idle Time");
        _cDiskQueue = _pdh.Add(@"\PhysicalDisk(_Total)\Avg. Disk Queue Length");
        _cDiskRead = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        _cDiskWrite = _pdh.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        _cPageReads = _pdh.Add(@"\Memory\Page Reads/sec");
        _cGpu = _pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
        GpuAvailable = _cGpu != IntPtr.Zero;
        // ACPI-Thermalzonen: ohne Adminrechte lesbar (Zehntel-Kelvin)
        _cTzTemp = _pdh.Add(@"\Thermal Zone Information(*)\High Precision Temperature");
        _cTzPassive = _pdh.Add(@"\Thermal Zone Information(*)\% Passive Limit");
        _cTzThrottle = _pdh.Add(@"\Thermal Zone Information(*)\Throttle Reasons");
        _pdh.Collect();

        Native.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
        (_prevRx, _prevTx) = ReadNetworkBytes();
        _prevNetTicks = Stopwatch.GetTimestamp();
        _processes.Sample(new Dictionary<int, double>());
    }

    private Snapshot Take()
    {
        var pdh = _pdh!;
        pdh.Collect();

        // CPU: echte Auslastung (Zeitanteil). "% Processor Utility" wird durch Turbo-Takt skaliert
        // und passt dann nicht mehr zur Summe der Prozesswerte.
        double cpu = CpuFromSystemTimes();

        var (gpuTotal, gpuPerPid) = ReadGpu(pdh);

        var mem = Native.GetMemoryStatus();

        long now = Stopwatch.GetTimestamp();
        double netSeconds = (now - _prevNetTicks) / (double)Stopwatch.Frequency;
        var (rx, tx) = ReadNetworkBytes();
        double down = netSeconds > 0 ? Math.Max(0, rx - _prevRx) / netSeconds : 0;
        double up = netSeconds > 0 ? Math.Max(0, tx - _prevTx) / netSeconds : 0;
        (_prevRx, _prevTx, _prevNetTicks) = (rx, tx, now);

        double? diskActive = pdh.Get(_cDiskIdle) is double idle ? Math.Clamp(100 - idle, 0, 100) : null;

        return new Snapshot
        {
            Time = DateTime.Now,
            Cpu = cpu,
            CpuPerformance = pdh.Get(_cPerf),
            CpuFrequencyMhz = pdh.Get(_cFreq) is double f && pdh.Get(_cPerf) is double p ? f * p / 100 : null,
            Dpc = pdh.Get(_cDpc),
            Interrupt = pdh.Get(_cInterrupt),
            ProcessorQueue = pdh.Get(_cQueue),
            RamTotal = mem.ullTotalPhys,
            RamUsed = mem.ullTotalPhys - mem.ullAvailPhys,
            CommitLimit = mem.ullTotalPageFile,
            CommitUsed = mem.ullTotalPageFile - mem.ullAvailPageFile,
            HardFaults = pdh.Get(_cPageReads),
            DiskActive = diskActive,
            DiskQueue = pdh.Get(_cDiskQueue),
            DiskReadBps = pdh.Get(_cDiskRead) ?? 0,
            DiskWriteBps = pdh.Get(_cDiskWrite) ?? 0,
            NetDownBps = down,
            NetUpBps = up,
            Gpu = gpuTotal,
            Processes = _processes.Sample(gpuPerPid),
            ThermalZones = ReadThermalZones(pdh),
        };
    }

    private List<ThermalZone> ReadThermalZones(PdhQuery pdh)
    {
        var zones = new List<ThermalZone>();
        if (_cTzTemp == IntPtr.Zero) return zones;
        var passive = pdh.GetArray(_cTzPassive).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Value);
        var throttle = pdh.GetArray(_cTzThrottle).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var (name, tenthKelvin) in pdh.GetArray(_cTzTemp))
        {
            double c = tenthKelvin / 10.0 - 273.15;
            if (c is < 5 or > 125) continue; // 0 °C (2732) = Sensor nicht belegt
            zones.Add(new ThermalZone(name, c,
                passive.TryGetValue(name, out var pl) ? pl : null,
                throttle.TryGetValue(name, out var tr) ? (int)tr : 0));
        }
        return zones;
    }

    private double CpuFromSystemTimes()
    {
        if (!Native.GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);
        long total = dKernel + dUser; // Kernel-Zeit enthält die Leerlaufzeit
        return total <= 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
    }

    /// <summary>GPU-Last wie im Task-Manager: pro Engine summiert, Gesamtwert = am stärksten ausgelastete Engine.</summary>
    private (double? Total, Dictionary<int, double> PerPid) ReadGpu(PdhQuery pdh)
    {
        var perPid = new Dictionary<int, double>();
        if (_cGpu == IntPtr.Zero) return (null, perPid);

        var engines = new Dictionary<string, double>();
        var pidEngine = new Dictionary<(int, string), double>();
        foreach (var (name, value) in pdh.GetArray(_cGpu))
        {
            if (value <= 0) continue;
            // Format: pid_1234_luid_0x00000000_0x0000C2B5_phys_0_eng_3_engtype_3D
            int pidStart = name.IndexOf("pid_", StringComparison.Ordinal);
            int luidStart = name.IndexOf("_luid_", StringComparison.Ordinal);
            int typeStart = name.IndexOf("_engtype_", StringComparison.Ordinal);
            if (pidStart < 0 || luidStart < 0 || typeStart < luidStart) continue;
            if (!int.TryParse(name.AsSpan(pidStart + 4, luidStart - pidStart - 4), out int pid)) continue;
            string engine = name[luidStart..typeStart];
            engines[engine] = engines.GetValueOrDefault(engine) + value;
            pidEngine[(pid, engine)] = pidEngine.GetValueOrDefault((pid, engine)) + value;
        }
        foreach (var ((pid, _), v) in pidEngine)
            perPid[pid] = Math.Max(perPid.GetValueOrDefault(pid), Math.Min(100, v));
        double total = engines.Count == 0 ? 0 : Math.Min(100, engines.Values.Max());
        return (total, perPid);
    }

    private static (long Rx, long Tx) ReadNetworkBytes()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Misst CPU, RAM und Datenträger-I/O pro Prozess über Differenzen zwischen zwei Messungen.</summary>
internal sealed class ProcessSampler
{
    private readonly record struct Previous(long Creation, long CpuTime, ulong IoRead, ulong IoWrite);

    private sealed class Meta
    {
        public string? Path;
        public string? Description;
        public ImageSource? Icon;
    }

    private readonly Dictionary<int, Previous> _previous = new();
    private readonly Dictionary<(int, long), Meta> _meta = new();
    private readonly int _cpuCount = Environment.ProcessorCount;
    private long _lastTicks;

    public List<ProcessInfo> Sample(IReadOnlyDictionary<int, double> gpuPerPid)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsed = _lastTicks == 0 ? 0 : (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        var list = new List<ProcessInfo>(400);
        var seenPids = new HashSet<int>();
        var seenMeta = new HashSet<(int, long)>();
        var memSize = (uint)Marshal.SizeOf<Native.PROCESS_MEMORY_COUNTERS_EX>();

        foreach (var proc in Process.GetProcesses())
        {
            using (proc)
            {
                int pid = proc.Id;
                if (pid == 0) continue; // Leerlaufprozess
                string name = proc.ProcessName;
                long workingSet = 0;
                int handles = 0;
                try
                {
                    workingSet = proc.WorkingSet64;
                    handles = proc.HandleCount;
                }
                catch { }
                seenPids.Add(pid);

                double cpu = 0, ioRead = 0, ioWrite = 0;
                long privateBytes = 0;
                Meta? meta = null;

                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        if (Native.GetProcessTimes(h, out long creation, out _, out long kernel, out long user))
                        {
                            long cpuTime = kernel + user;
                            Native.GetProcessIoCounters(h, out var io);
                            if (_previous.TryGetValue(pid, out var prev) && prev.Creation == creation && elapsed > 0)
                            {
                                cpu = Math.Clamp((cpuTime - prev.CpuTime) / (elapsed * 1e7 * _cpuCount) * 100, 0, 100);
                                if (io.ReadTransferCount >= prev.IoRead) ioRead = (io.ReadTransferCount - prev.IoRead) / elapsed;
                                if (io.WriteTransferCount >= prev.IoWrite) ioWrite = (io.WriteTransferCount - prev.IoWrite) / elapsed;
                            }
                            _previous[pid] = new Previous(creation, cpuTime, io.ReadTransferCount, io.WriteTransferCount);

                            var key = (pid, creation);
                            seenMeta.Add(key);
                            if (!_meta.TryGetValue(key, out meta))
                            {
                                meta = LoadMeta(h);
                                _meta[key] = meta;
                            }
                        }
                        if (Native.K32GetProcessMemoryInfo(h, out var mc, memSize))
                            privateBytes = (long)mc.PrivateUsage;
                    }
                    finally
                    {
                        Native.CloseHandle(h);
                    }
                }

                list.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    Path = meta?.Path,
                    Description = meta?.Description,
                    Icon = meta?.Icon ?? IconCache.Default,
                    Cpu = cpu,
                    WorkingSet = workingSet,
                    PrivateBytes = privateBytes,
                    IoReadBps = ioRead,
                    IoWriteBps = ioWrite,
                    Gpu = gpuPerPid.GetValueOrDefault(pid),
                    Handles = handles,
                });
            }
        }

        foreach (var pid in _previous.Keys.Where(p => !seenPids.Contains(p)).ToList()) _previous.Remove(pid);
        foreach (var key in _meta.Keys.Where(k => !seenMeta.Contains(k)).ToList()) _meta.Remove(key);
        return list;
    }

    private static Meta LoadMeta(IntPtr handle)
    {
        var meta = new Meta { Path = Native.GetProcessPath(handle) };
        if (meta.Path != null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(meta.Path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) meta.Description = desc.Trim();
            }
            catch { }
            meta.Icon = IconCache.Get(meta.Path);
        }
        return meta;
    }
}
