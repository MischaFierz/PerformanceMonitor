using System.Management;
using System.Windows.Media;

namespace PerformanceMonitor.Core;

public enum HeatKind { Cpu, Gpu, Ssd, Ram, Battery, Chipset, Charger, Board, Skin, Ambient, Other }

public enum HeatLevel { Cool, Normal, Warm, Hot, Critical }

public sealed record TempSensor(string Id, string Label, HeatKind Kind, double Celsius, bool Estimated, string Source, double? PassiveLimit = null)
{
    public HeatLevel Level => TemperatureService.LevelFor(Kind, Celsius);
    public string ValueText => $"{(Estimated ? "≈ " : "")}{Celsius:0} °C";
    public bool IsThrottling => PassiveLimit is < 99.5;
}

/// <summary>
/// Liest Temperaturen ohne Kernel-Treiber: ACPI-Thermalzonen (ohne Adminrechte), SSD-Temperatur (mit Adminrechten).
/// Für Bauteile ohne Sensor wird die Temperatur aus der Auslastung geschätzt und als solche gekennzeichnet.
/// </summary>
public static class TemperatureService
{
    private static double? _diskTemp;
    private static DateTime _diskRead = DateTime.MinValue;
    private static bool _diskReading;

    public static (HeatKind Kind, string Label) ClassifyZone(string zoneName)
    {
        var n = zoneName.ToUpperInvariant().Replace(@"\_TZ.", "").Replace("_TZ.", "");
        if (n.Contains("CPU") || n.Contains("PROC") || n.StartsWith("CPUZ")) return (HeatKind.Cpu, "Prozessor");
        if (n.Contains("GFX") || n.Contains("GPU") || n.Contains("VGA")) return (HeatKind.Gpu, "Grafik");
        if (n.Contains("BAT")) return (HeatKind.Battery, "Akku");
        if (n.Contains("CHG") || n.Contains("CHRG") || n.Contains("CHARG")) return (HeatKind.Charger, "Ladeelektronik");
        if (n.StartsWith("SK") || n.Contains("SKIN")) return (HeatKind.Skin, $"Gehäuseoberfläche ({n})");
        if (n.Contains("PCH") || n.Contains("CHIP")) return (HeatKind.Chipset, "Chipsatz (PCH)");
        if (n.Contains("MEM") || n.Contains("DIMM") || n.Contains("RAM")) return (HeatKind.Ram, "Arbeitsspeicher");
        if (n.Contains("SSD") || n.Contains("NVM") || n.Contains("STOR")) return (HeatKind.Ssd, "SSD");
        if (n.Contains("EXT") || n.Contains("AMB")) return (HeatKind.Ambient, "Umgebung / Lufteinlass");
        if (n.Contains("LOC") || n.Contains("SYS") || n.Contains("TZ0")) return (HeatKind.Board, "Mainboard");
        return (HeatKind.Other, $"Weiterer Sensor ({n})");
    }

    /// <summary>Alle Temperaturen für die Anzeige – gemessene zuerst, fehlende Hauptbauteile geschätzt.</summary>
    public static List<TempSensor> Build(Snapshot s)
    {
        var list = new List<TempSensor>();
        foreach (var z in s.ThermalZones)
        {
            var (kind, label) = ClassifyZone(z.Name);
            list.Add(new TempSensor(z.Name, label, kind, z.Celsius, false, $"ACPI-Thermalzone {z.Name.Replace(@"\_TZ.", "")}", z.PassiveLimit));
        }

        RefreshDiskTempAsync();
        if (_diskTemp is double dt)
            list.Add(new TempSensor("ssd", "SSD", HeatKind.Ssd, dt, false, "S.M.A.R.T. (Datenträger)"));

        // Schätzungen für Bauteile ohne Sensor
        double ambient = list.FirstOrDefault(t => t.Kind == HeatKind.Ambient)?.Celsius ?? 30;
        if (list.All(t => t.Kind != HeatKind.Cpu))
            list.Add(new TempSensor("cpu-est", "Prozessor", HeatKind.Cpu, ambient + 8 + s.Cpu * 0.5, true, "geschätzt aus CPU-Auslastung (kein Sensor verfügbar)"));
        if (list.All(t => t.Kind != HeatKind.Gpu) && s.Gpu is double g)
            list.Add(new TempSensor("gpu-est", "Grafik", HeatKind.Gpu, ambient + 6 + g * 0.45, true, "geschätzt aus GPU-Auslastung (kein Sensor verfügbar)"));
        if (list.All(t => t.Kind != HeatKind.Ssd))
            list.Add(new TempSensor("ssd-est", "SSD", HeatKind.Ssd, ambient + 4 + (s.DiskActive ?? 0) * 0.25, true,
                App.IsAdmin ? "geschätzt aus Datenträgerlast" : "geschätzt aus Datenträgerlast – genauer Wert mit Adminrechten"));
        if (list.All(t => t.Kind != HeatKind.Ram))
            list.Add(new TempSensor("ram-est", "Arbeitsspeicher", HeatKind.Ram, ambient + 4 + s.RamPercent * 0.08 + s.Cpu * 0.05, true, "geschätzt aus Auslastung (kein Sensor verfügbar)"));

        return list.OrderBy(t => t.Estimated).ThenByDescending(t => t.Celsius).ToList();
    }

    private static void RefreshDiskTempAsync()
    {
        if (!App.IsAdmin || _diskReading || DateTime.Now - _diskRead < TimeSpan.FromSeconds(60)) return;
        _diskReading = true;
        Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT Temperature FROM MSFT_StorageReliabilityCounter");
                double? max = null;
                foreach (var o in searcher.Get())
                {
                    var t = Convert.ToDouble(o["Temperature"] ?? 0);
                    if (t is > 5 and < 110) max = Math.Max(max ?? 0, t);
                }
                _diskTemp = max;
            }
            catch { }
            finally
            {
                _diskRead = DateTime.Now;
                _diskReading = false;
            }
        });
    }

    // Schwellen je Bauteil: (warm, heiss, kritisch)
    private static (double Warm, double Hot, double Critical) Limits(HeatKind kind) => kind switch
    {
        HeatKind.Cpu or HeatKind.Gpu => (70, 85, 95),
        HeatKind.Ssd => (55, 65, 72),
        HeatKind.Battery => (40, 45, 50),
        HeatKind.Skin => (43, 48, 54),
        HeatKind.Ram => (55, 70, 80),
        HeatKind.Ambient => (35, 40, 45),
        _ => (60, 75, 88),
    };

    public static HeatLevel LevelFor(HeatKind kind, double c)
    {
        var (warm, hot, crit) = Limits(kind);
        if (c >= crit) return HeatLevel.Critical;
        if (c >= hot) return HeatLevel.Hot;
        if (c >= warm) return HeatLevel.Warm;
        return c < warm - 25 ? HeatLevel.Cool : HeatLevel.Normal;
    }

    public static string LevelText(HeatLevel l) => l switch
    {
        HeatLevel.Cool => "kühl",
        HeatLevel.Normal => "normal",
        HeatLevel.Warm => "warm",
        HeatLevel.Hot => "heiss",
        _ => "kritisch",
    };

    private static readonly (double T, Color C)[] Stops =
    [
        (25, Color.FromRgb(0x3B, 0x82, 0xF6)),
        (40, Color.FromRgb(0x22, 0xD3, 0xEE)),
        (55, Color.FromRgb(0x22, 0xC5, 0x5E)),
        (68, Color.FromRgb(0xFA, 0xCC, 0x15)),
        (80, Color.FromRgb(0xF9, 0x73, 0x16)),
        (92, Color.FromRgb(0xEF, 0x44, 0x44)),
    ];

    public static (double Min, double Max) ScaleRange => (Stops[0].T, Stops[^1].T);

    /// <summary>Farbe für die Hitze-Darstellung (blau = kühl … rot = heiss).</summary>
    public static Color ColorFor(double c)
    {
        if (c <= Stops[0].T) return Stops[0].C;
        for (int i = 1; i < Stops.Length; i++)
        {
            if (c > Stops[i].T) continue;
            double f = (c - Stops[i - 1].T) / (Stops[i].T - Stops[i - 1].T);
            var a = Stops[i - 1].C;
            var b = Stops[i].C;
            return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
        }
        return Stops[^1].C;
    }

    public static IEnumerable<(double T, Color C)> GradientStops => Stops;
}

public sealed class ThermalTestResult
{
    public int Seconds { get; init; }
    public double StartTemp { get; init; }
    public double MaxTemp { get; init; }
    public double EndCooldownTemp { get; init; }
    public double AvgLoad { get; init; }
    public double PerfStart { get; init; }
    public double PerfEnd { get; init; }
    public bool Throttled { get; init; }
    public bool TempMeasured { get; init; }
    public string Verdict { get; init; } = "";
    public string Detail { get; init; } = "";
    public Severity Severity { get; init; }
}

/// <summary>Belastet alle CPU-Kerne für kurze Zeit und beobachtet Temperaturanstieg, Drosselung und Abkühlung.</summary>
public static class ThermalTest
{
    public static async Task<ThermalTestResult> RunAsync(int loadSeconds, IProgress<(double Fraction, string Text)> progress, CancellationToken ct)
    {
        var samples = new List<(Snapshot S, bool Load)>();
        bool loading = false;
        void Handler(Snapshot s) { lock (samples) samples.Add((s, loading)); }

        double CpuTemp(Snapshot s) => TemperatureService.Build(s).First(t => t.Kind == HeatKind.Cpu).Celsius;

        var start = App.Monitor.Latest ?? throw new InvalidOperationException("Noch keine Messwerte vorhanden.");
        bool measured = start.ThermalZones.Any(z => TemperatureService.ClassifyZone(z.Name).Kind == HeatKind.Cpu);
        double startTemp = CpuTemp(start);

        App.Monitor.SnapshotReady += Handler;
        using var stop = new CancellationTokenSource();
        var threads = new List<Thread>();
        try
        {
            loading = true;
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                var t = new Thread(() =>
                {
                    double x = 1.0001;
                    while (!stop.IsCancellationRequested)
                        for (int k = 0; k < 200_000; k++) x = Math.Sqrt(x * 1.000001 + k) % 1000 + 1;
                    GC.KeepAlive(x);
                }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                threads.Add(t);
                t.Start();
            }

            int cool = 10;
            for (int sec = 0; sec < loadSeconds + cool; sec++)
            {
                if (sec == loadSeconds)
                {
                    stop.Cancel();
                    loading = false;
                }
                var latest = App.Monitor.Latest;
                string temp = latest != null ? $"{(measured ? "" : "≈ ")}{CpuTemp(latest):0} °C" : "–";
                progress.Report(((double)sec / (loadSeconds + cool),
                    sec < loadSeconds ? $"Belaste alle {Environment.ProcessorCount} Threads … noch {loadSeconds - sec} s  ·  CPU {temp}"
                                      : $"Abkühlphase … noch {loadSeconds + cool - sec} s  ·  CPU {temp}"));
                await Task.Delay(1000, ct);
            }
        }
        finally
        {
            stop.Cancel();
            App.Monitor.SnapshotReady -= Handler;
        }

        List<(Snapshot S, bool Load)> copy;
        lock (samples) copy = samples.ToList();
        var load = copy.Where(x => x.Load).Select(x => x.S).ToList();
        var after = copy.Where(x => !x.Load).Select(x => x.S).ToList();
        if (load.Count < 3) throw new InvalidOperationException("Zu wenige Messwerte während des Tests.");

        double maxTemp = load.Max(CpuTemp);
        double endTemp = after.Count > 0 ? CpuTemp(after[^1]) : maxTemp;
        var firstPart = load.Take(Math.Max(1, load.Count / 4)).ToList();
        var lastPart = load.Skip(load.Count * 3 / 4).ToList();
        double perfStart = firstPart.Average(s => s.CpuPerformance ?? 100);
        double perfEnd = lastPart.Average(s => s.CpuPerformance ?? 100);
        bool passive = load.Any(s => s.ThermalZones.Any(z => z.PassiveLimit is < 99.5));
        bool throttled = passive || perfEnd < perfStart * 0.8;

        string tempLabel = measured ? "" : "≈ ";
        Severity sev;
        string verdict;
        if (throttled && maxTemp >= 90) { sev = Severity.Critical; verdict = "Kühlung überfordert – der Prozessor drosselt wegen Hitze"; }
        else if (throttled) { sev = Severity.Warning; verdict = "Der Prozessor drosselt unter Last"; }
        else if (maxTemp >= 95) { sev = Severity.Critical; verdict = "Sehr hohe Temperatur unter Last"; }
        else if (maxTemp >= 85) { sev = Severity.Warning; verdict = "Kühlung arbeitet am Limit"; }
        else if (maxTemp >= 75) { sev = Severity.Info; verdict = "Kühlung in Ordnung"; }
        else { sev = Severity.Ok; verdict = "Kühlung sehr gut"; }

        string detail = $"Start {tempLabel}{startTemp:0} °C → Maximum {tempLabel}{maxTemp:0} °C (+{maxTemp - startTemp:0} K) in {loadSeconds} s Volllast. "
                        + $"Leistung am Anfang {perfStart:0} %, am Ende {perfEnd:0} % der Nennleistung. "
                        + (after.Count > 0 ? $"Nach 10 s Abkühlung: {tempLabel}{endTemp:0} °C." : "")
                        + (passive ? " Windows meldet aktive passive Kühlung (thermische Drosselung)." : "")
                        + (measured ? "" : " Hinweis: Kein CPU-Temperatursensor verfügbar – Temperaturen sind geschätzt, die Drosselungserkennung basiert auf der gemessenen Leistung.");

        return new ThermalTestResult
        {
            Seconds = loadSeconds,
            StartTemp = startTemp,
            MaxTemp = maxTemp,
            EndCooldownTemp = endTemp,
            AvgLoad = load.Average(s => s.Cpu),
            PerfStart = perfStart,
            PerfEnd = perfEnd,
            Throttled = throttled,
            TempMeasured = measured,
            Verdict = verdict,
            Detail = detail,
            Severity = sev,
        };
    }
}
