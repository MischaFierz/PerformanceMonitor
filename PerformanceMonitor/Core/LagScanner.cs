using System.Text.Json;
using System.Windows.Media;

namespace PerformanceMonitor.Core;

public enum Severity { Ok = 0, Info = 1, Warning = 2, Critical = 3 }

public sealed class Finding : ObservableObject
{
    public Severity Severity { get; init; }
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? Recommendation { get; init; }
    public string? ActionLabel { get; init; }
    public string? ConfirmText { get; init; }
    public Func<Task<string?>>? Action { get; init; }

    public bool HasAction => Action != null && ActionLabel != null;
    public bool HasRecommendation => !string.IsNullOrEmpty(Recommendation);

    private string _actionStatus = "";
    public string ActionStatus { get => _actionStatus; set => SetField(ref _actionStatus, value); }

    private bool _actionDone;
    public bool ActionDone { get => _actionDone; set => SetField(ref _actionDone, value); }
}

public sealed class Culprit
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Path { get; init; }
    public ImageSource? Icon { get; init; }
    public int Count { get; init; }
    public double AvgCpu { get; init; }
    public double PeakCpu { get; init; }
    public long Ram { get; init; }
    public double AvgIo { get; init; }
    public double AvgGpu { get; init; }
    public double Impact { get; init; }
    public string? Hint { get; init; }

    public string SubText => Count > 1 ? $"{Name} · {Count} Prozesse" : Name;
    public string CpuText => $"{AvgCpu:0.0} %";
    public string PeakText => $"{PeakCpu:0} %";
    public string RamText => Fmt.Bytes(Ram);
    public string IoText => Fmt.MBps(AvgIo);
    public string GpuText => $"{AvgGpu:0} %";
    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

public sealed class ScanResult
{
    public DateTime Time { get; init; }
    public int DurationSec { get; init; }
    public int Samples { get; init; }
    public int Score { get; init; }
    public string Rating { get; init; } = "";
    public string Headline { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<Finding> Findings { get; init; } = new();
    public List<Culprit> Culprits { get; init; } = new();
    public double AvgCpu { get; init; }
    public double PeakCpu { get; init; }
    public double AvgRam { get; init; }
    public double? AvgDisk { get; init; }
    public double? AvgGpu { get; init; }
    public double? AvgDpc { get; init; }
}

public readonly record struct ScanProgress(double Fraction, string Message, string Live);

public sealed class LagScanner(SystemMonitor monitor)
{
    private const double MB = 1024 * 1024;
    private const double GB = 1024 * MB;

    public async Task<ScanResult> RunAsync(int seconds, IProgress<ScanProgress> progress, CancellationToken ct)
    {
        var samples = new List<Snapshot>();
        void OnSnapshot(Snapshot s) { lock (samples) samples.Add(s); }

        // Konfigurationsprüfungen parallel zur Messung starten
        var staticTask = Task.Run(CollectStaticInfo, ct);

        monitor.SnapshotReady += OnSnapshot;
        try
        {
            var start = DateTime.Now;
            string[] phases = ["Messe Prozessorlast", "Messe Arbeitsspeicher", "Messe Datenträger", "Messe Grafikkarte", "Prüfe Treiber-Latenz", "Analysiere Prozesse"];
            while (true)
            {
                double elapsed = (DateTime.Now - start).TotalSeconds;
                if (elapsed >= seconds) break;
                var phase = phases[Math.Min(phases.Length - 1, (int)(elapsed / seconds * phases.Length))];
                var latest = monitor.Latest;
                string live = latest == null ? "" : $"CPU {latest.Cpu:0} %  ·  RAM {latest.RamPercent:0} %  ·  Disk {Fmt.Percent(latest.DiskActive)}";
                progress.Report(new ScanProgress(elapsed / seconds * 0.85, $"{phase} … noch {Math.Ceiling(seconds - elapsed)} s", live));
                await Task.Delay(200, ct);
            }
        }
        finally
        {
            monitor.SnapshotReady -= OnSnapshot;
        }

        progress.Report(new ScanProgress(0.9, "Prüfe Systemkonfiguration …", ""));
        var info = await staticTask;
        List<Snapshot> copy;
        lock (samples) copy = samples.ToList();
        if (copy.Count == 0 && monitor.Latest != null) copy.Add(monitor.Latest);
        if (copy.Count == 0) throw new InvalidOperationException("Es konnten keine Messwerte gesammelt werden.");

        progress.Report(new ScanProgress(0.97, "Werte Ergebnisse aus …", ""));
        var result = await Task.Run(() => Analyze(copy, info, seconds), ct);
        progress.Report(new ScanProgress(1, "Fertig", ""));
        return result;
    }

    private sealed class StaticInfo
    {
        public Guid? ActivePlan;
        public string ActivePlanName = "";
        public PowerPlan? BestPlan;
        public List<DriveRow> Drives = new();
        public string? SystemDiskType;
        public int StartupEnabled;
        public long TempSize;
        public double? ThermalC;
        public bool OnBattery;
        public TimeSpan Uptime;
    }

    private static StaticInfo CollectStaticInfo()
    {
        var info = new StaticInfo { Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) };
        try
        {
            var plans = PowerPlans.GetAll();
            var active = plans.FirstOrDefault(p => p.IsActive);
            info.ActivePlan = active?.Id;
            info.ActivePlanName = active?.Name ?? "";
            info.BestPlan = plans.FirstOrDefault(p => p.Id == PowerPlans.UltimatePerformance) ?? plans.FirstOrDefault(p => p.Id == PowerPlans.HighPerformance);
        }
        catch { }
        try
        {
            var types = SystemInfoService.GetDiskTypes();
            info.Drives = SystemInfoService.GetDrives(types);
            info.SystemDiskType = info.Drives.FirstOrDefault(d => d.IsSystem)?.Type;
        }
        catch { }
        try { info.StartupEnabled = StartupManager.Load().Count(e => e.IsEnabled); } catch { }
        try { info.TempSize = CleanupService.MeasureFolder(Path.GetTempPath()); } catch { }
        try { if (App.IsAdmin) info.ThermalC = SystemInfoService.TryGetThermalCelsius(); } catch { }
        try { info.OnBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
        return info;
    }

    private sealed class Acc
    {
        public string Name = "";
        public string? Description;
        public string? Path;
        public ImageSource? Icon;
        public double CpuSum, CpuPeak, IoSum, GpuSum;
        public double RamSum;
        public int Count;
    }

    private static List<Culprit> Aggregate(List<Snapshot> samples)
    {
        int ownPid = Environment.ProcessId;
        var acc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in samples)
        {
            var perSample = new Dictionary<string, (double Cpu, long Ram, double Io, double Gpu, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s.Processes)
            {
                if (p.Pid == ownPid) continue;
                var cur = perSample.GetValueOrDefault(p.Name);
                perSample[p.Name] = (cur.Cpu + p.Cpu, cur.Ram + p.WorkingSet, cur.Io + p.IoBps, cur.Gpu + p.Gpu, cur.Count + 1);
                if (!acc.TryGetValue(p.Name, out var a))
                    acc[p.Name] = a = new Acc { Name = p.Name };
                a.Description ??= p.Description;
                a.Path ??= p.Path;
                if (a.Icon == null || ReferenceEquals(a.Icon, IconCache.Default)) a.Icon = p.Icon;
            }
            foreach (var (name, v) in perSample)
            {
                var a = acc[name];
                a.CpuSum += v.Cpu;
                a.CpuPeak = Math.Max(a.CpuPeak, v.Cpu);
                a.RamSum += v.Ram;
                a.IoSum += v.Io;
                a.GpuSum += Math.Min(100, v.Gpu);
                a.Count = v.Count;
            }
        }

        int n = samples.Count;
        double totalRam = samples[^1].RamTotal;
        return acc.Values.Select(a =>
        {
            double cpu = a.CpuSum / n, ram = a.RamSum / n, io = a.IoSum / n, gpu = a.GpuSum / n;
            double impact = cpu + (totalRam > 0 ? ram / totalRam * 100 * 0.4 : 0) + Math.Min(40, io / MB * 2) + gpu * 0.6 + a.CpuPeak * 0.1;
            return new Culprit
            {
                Name = a.Name,
                DisplayName = string.IsNullOrWhiteSpace(a.Description) ? a.Name : a.Description!,
                Path = a.Path,
                Icon = a.Icon,
                Count = a.Count,
                AvgCpu = cpu,
                PeakCpu = a.CpuPeak,
                Ram = (long)ram,
                AvgIo = io,
                AvgGpu = gpu,
                Impact = impact,
                Hint = KnownProcesses.GetHint(a.Name),
            };
        }).OrderByDescending(c => c.Impact).ToList();
    }

    private static double? AvgOf(List<Snapshot> samples, Func<Snapshot, double?> selector)
    {
        var values = samples.Select(selector).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static Finding KillFinding(Severity severity, string category, string title, string detail, Culprit c)
    {
        bool critical = KnownProcesses.IsCritical(c.Name);
        var recommendation = c.Hint ?? (critical
            ? "Systemprozess – nicht beenden. Ein Neustart oder Treiber-/Windows-Updates helfen meist."
            : "Schliesse das Programm, wenn du es gerade nicht brauchst.");
        if (c.Hint != null && !critical) recommendation += " Bei Bedarf kannst du es direkt beenden.";
        return new Finding
        {
            Severity = severity,
            Category = category,
            Title = title,
            Detail = detail,
            Recommendation = recommendation,
            ActionLabel = critical ? null : (c.Count > 1 ? "Alle beenden" : "Beenden"),
            ConfirmText = $"„{c.DisplayName}“ ({c.Name}, {c.Count} Prozess{(c.Count > 1 ? "e" : "")}) wirklich beenden?\n\nNicht gespeicherte Daten gehen dabei verloren.",
            Action = critical ? null : () => Task.Run<string?>(() =>
            {
                var (ok, failed) = ProcessActions.KillByName(c.Name);
                return failed == 0 ? $"{ok} beendet ✓" : $"{ok} beendet, {failed} fehlgeschlagen (Adminrechte?)";
            }),
        };
    }

    private static Finding NavigateFinding(Severity severity, string category, string title, string detail, string recommendation, string label, string page) => new()
    {
        Severity = severity,
        Category = category,
        Title = title,
        Detail = detail,
        Recommendation = recommendation,
        ActionLabel = label,
        Action = () =>
        {
            App.Navigate(page);
            return Task.FromResult<string?>(null);
        },
    };

    private static ScanResult Analyze(List<Snapshot> samples, StaticInfo info, int seconds)
    {
        var findings = new List<Finding>();
        var last = samples[^1];
        double totalRam = last.RamTotal;
        int cores = Environment.ProcessorCount;

        double avgCpu = samples.Average(s => s.Cpu);
        double peakCpu = samples.Max(s => s.Cpu);
        double avgRam = samples.Average(s => s.RamPercent);
        double avgCommit = samples.Average(s => s.CommitPercent);
        double? avgDisk = AvgOf(samples, s => s.DiskActive);
        double? avgQueue = AvgOf(samples, s => s.DiskQueue);
        double? avgGpu = AvgOf(samples, s => s.Gpu);
        double? avgDpc = AvgOf(samples, s => s.Dpc);
        double? avgInt = AvgOf(samples, s => s.Interrupt);
        double? avgPerf = AvgOf(samples, s => s.CpuPerformance);
        double? avgFaults = AvgOf(samples, s => s.HardFaults);
        double? avgProcQueue = AvgOf(samples, s => s.ProcessorQueue);
        double avgNet = samples.Average(s => s.NetDownBps + s.NetUpBps);

        var culprits = Aggregate(samples);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------- Prozessor ----------
        var topCpu = culprits.OrderByDescending(c => c.AvgCpu).Take(3).Where(c => c.AvgCpu >= 3).ToList();
        string topCpuText = topCpu.Count == 0 ? "" : " Hauptlast: " + string.Join(", ", topCpu.Select(c => $"{c.DisplayName} ({c.AvgCpu:0} %)")) + ".";
        if (avgCpu >= 85)
            findings.Add(new Finding { Severity = Severity.Critical, Category = "Prozessor", Title = "Prozessor ist dauerhaft voll ausgelastet", Detail = $"Durchschnitt {avgCpu:0} %, Spitze {peakCpu:0} %.{topCpuText}", Recommendation = "Beende rechenintensive Programme, die du nicht brauchst. Bleibt die Last hoch, prüfe auf Malware oder hängende Hintergrundprozesse." });
        else if (avgCpu >= 60)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Prozessor", Title = "Hohe Prozessorlast", Detail = $"Durchschnitt {avgCpu:0} %, Spitze {peakCpu:0} %.{topCpuText}", Recommendation = "Schliesse Programme mit hoher CPU-Last, um Ruckler zu vermeiden." });
        else if (peakCpu >= 95 && avgCpu < 40)
            findings.Add(new Finding { Severity = Severity.Info, Category = "Prozessor", Title = "Kurze CPU-Spitzen erkannt", Detail = $"Die CPU erreichte kurzzeitig {peakCpu:0} % (Durchschnitt {avgCpu:0} %). Solche Spitzen verursachen kurze Hänger.", Recommendation = "Siehe Hauptverursacher unten – meist sind es Updates, Virenscans oder Browser." });

        if (avgProcQueue is double pq && pq > cores * 2 && avgCpu > 50)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Prozessor", Title = "Prozesse warten auf CPU-Zeit", Detail = $"Durchschnittlich {pq:0} Threads in der Warteschlange bei {cores} logischen Kernen.", Recommendation = "Das System ist überlastet. Weniger Programme gleichzeitig ausführen." });

        foreach (var c in culprits.Where(c => c.AvgCpu >= 15).Take(4))
        {
            var sev = c.AvgCpu >= 40 ? Severity.Critical : Severity.Warning;
            findings.Add(KillFinding(sev, "Prozess", $"{c.DisplayName} belastet den Prozessor", $"Ø {c.AvgCpu:0.0} % CPU, Spitze {c.PeakCpu:0} % ({c.SubText}).", c));
            reported.Add(c.Name);
        }
        foreach (var c in culprits.Where(c => c.PeakCpu >= 50 && c.AvgCpu < 15 && !reported.Contains(c.Name)).OrderByDescending(c => c.PeakCpu).Take(2))
        {
            findings.Add(KillFinding(Severity.Info, "Prozess", $"CPU-Spitzen durch {c.DisplayName}", $"Kurzzeitig bis {c.PeakCpu:0} % CPU (Ø {c.AvgCpu:0.0} %). Das kann zu kurzen Rucklern führen.", c));
            reported.Add(c.Name);
        }

        if (avgPerf is double perf && perf < 65 && avgCpu > 40)
            findings.Add(new Finding
            {
                Severity = Severity.Warning, Category = "Prozessor", Title = "Prozessor taktet herunter",
                Detail = $"Unter Last lief die CPU nur mit ca. {perf:0} % ihrer Nennleistung. Mögliche Ursachen: Überhitzung, Energiesparmodus oder Akkubetrieb.",
                Recommendation = "Lüfter und Kühlung reinigen, Netzteil anschliessen und einen leistungsstärkeren Energiemodus wählen.",
            });

        // Temperatur: ACPI-Thermalzonen (ohne Adminrechte), sonst WMI als Admin
        var cpuZoneTemps = samples.SelectMany(s => s.ThermalZones)
            .Where(z => TemperatureService.ClassifyZone(z.Name).Kind == HeatKind.Cpu)
            .Select(z => z.Celsius).ToList();
        double? temp = cpuZoneTemps.Count > 0 ? cpuZoneTemps.Max() : info.ThermalC;
        bool thermalThrottle = samples.Any(s => s.ThermalZones.Any(z => z.PassiveLimit is < 99.5));
        Finding TempFinding(Severity sev, string title, string detail) => NavigateFinding(sev, "Temperatur", title, detail,
            "Lüftungsschlitze freihalten, Lüfter von Staub befreien, Laptop auf eine harte Unterlage stellen, ggf. Wärmeleitpaste erneuern.", "Temperaturen ansehen", "temps");
        if (thermalThrottle)
            findings.Add(TempFinding(Severity.Critical, "Thermische Drosselung aktiv",
                $"Windows reduziert die Leistung, weil das Gerät zu heiss ist{(temp is double t0 ? $" (Prozessor bis {t0:0} °C)" : "")}. Das führt direkt zu Lags."));
        else if (temp is double t1 && t1 >= 90)
            findings.Add(TempFinding(Severity.Critical, "Prozessor ist sehr heiss", $"Gemessen bis {t1:0} °C. Bei dieser Hitze drosselt der Prozessor die Leistung."));
        else if (temp is double t2 && t2 >= 80)
            findings.Add(TempFinding(Severity.Warning, "Erhöhte Prozessor-Temperatur", $"Gemessen bis {t2:0} °C."));

        // ---------- Arbeitsspeicher ----------
        var topRam = culprits.OrderByDescending(c => c.Ram).Take(3).ToList();
        string topRamText = " Grösste Verbraucher: " + string.Join(", ", topRam.Select(c => $"{c.DisplayName} ({Fmt.Bytes(c.Ram)})")) + ".";
        if (avgRam >= 90)
            findings.Add(new Finding { Severity = Severity.Critical, Category = "Arbeitsspeicher", Title = "Arbeitsspeicher ist fast voll", Detail = $"Ø {avgRam:0} % von {Fmt.Bytes(totalRam)} belegt.{topRamText}", Recommendation = "Programme schliessen. Wenn das regelmässig passiert, lohnt sich eine RAM-Aufrüstung." });
        else if (avgRam >= 80)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Arbeitsspeicher", Title = "Arbeitsspeicher wird knapp", Detail = $"Ø {avgRam:0} % von {Fmt.Bytes(totalRam)} belegt.{topRamText}", Recommendation = "Schliesse speicherhungrige Programme wie Browser mit vielen Tabs." });

        foreach (var c in culprits.Where(c => totalRam > 0 && c.Ram >= totalRam * 0.2 && !reported.Contains(c.Name)).Take(2))
        {
            findings.Add(KillFinding(avgRam >= 80 ? Severity.Warning : Severity.Info, "Prozess", $"{c.DisplayName} belegt viel Arbeitsspeicher", $"{Fmt.Bytes(c.Ram)} ({c.Ram / totalRam * 100:0} % des RAMs, {c.SubText}).", c));
            reported.Add(c.Name);
        }

        if (avgCommit >= 90)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Arbeitsspeicher", Title = "Virtueller Speicher fast erschöpft", Detail = $"Zugesicherter Speicher: {Fmt.Bytes(last.CommitUsed)} von {Fmt.Bytes(last.CommitLimit)} ({avgCommit:0} %).", Recommendation = "Programme schliessen oder die Auslagerungsdatei von Windows verwalten lassen." });

        if (avgFaults is double faults && faults > 400 && avgRam > 75)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Arbeitsspeicher", Title = "System lagert Speicher auf den Datenträger aus", Detail = $"Ø {faults:0} Seitenlesevorgänge pro Sekunde. Auslagern ist sehr viel langsamer als RAM und führt zu starken Rucklern.", Recommendation = "Mehr Arbeitsspeicher freigeben oder aufrüsten." });

        if (totalRam > 0 && totalRam < 7.5 * GB)
            findings.Add(new Finding { Severity = Severity.Info, Category = "Hardware", Title = "Wenig Arbeitsspeicher verbaut", Detail = $"Installiert: {Fmt.Bytes(totalRam)}. Für Windows 11 mit Browser und Spielen sind 16 GB empfehlenswert.", Recommendation = "Eine RAM-Aufrüstung bringt auf diesem Gerät spürbar mehr Leistung." });

        // ---------- Datenträger ----------
        var topIo = culprits.Where(c => c.AvgIo >= 3 * MB).OrderByDescending(c => c.AvgIo).Take(3).ToList();
        string topIoText = topIo.Count == 0 ? "" : " Verursacher: " + string.Join(", ", topIo.Select(c => $"{c.DisplayName} ({Fmt.MBps(c.AvgIo)})")) + ".";
        if (avgDisk is double disk)
        {
            string queueText = avgQueue is double q ? $", Warteschlange {q:0.0}" : "";
            if (disk >= 90)
                findings.Add(new Finding { Severity = Severity.Critical, Category = "Datenträger", Title = "Datenträger ist überlastet", Detail = $"Ø {disk:0} % aktive Zeit{queueText}.{topIoText}", Recommendation = "Warte laufende Updates/Downloads ab oder beende die Verursacher. Eine SSD beseitigt dieses Problem meist vollständig." });
            else if (disk >= 60)
                findings.Add(new Finding { Severity = Severity.Warning, Category = "Datenträger", Title = "Hohe Datenträgerlast", Detail = $"Ø {disk:0} % aktive Zeit{queueText}.{topIoText}", Recommendation = "Hintergrund-Synchronisierungen, Virenscans oder Updates können die Ursache sein." });
        }
        foreach (var c in topIo.Where(c => c.AvgIo >= 10 * MB && !reported.Contains(c.Name)).Take(2))
        {
            findings.Add(KillFinding(Severity.Warning, "Prozess", $"{c.DisplayName} liest/schreibt viele Daten", $"Ø {Fmt.MBps(c.AvgIo)} Datenträgerzugriffe ({c.SubText}).", c));
            reported.Add(c.Name);
        }

        if (info.SystemDiskType == "HDD")
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Hardware", Title = "Windows läuft auf einer Festplatte (HDD)", Detail = "Herkömmliche Festplatten sind um ein Vielfaches langsamer als SSDs – das ist eine der häufigsten Ursachen für ein träges System.", Recommendation = "Ein Umstieg auf eine SSD ist das wirkungsvollste Upgrade für mehr Geschwindigkeit." });

        var sys = info.Drives.FirstOrDefault(d => d.IsSystem);
        if (sys != null)
        {
            double freePct = sys.Total == 0 ? 100 : sys.Free * 100.0 / sys.Total;
            string detail = $"Auf {sys.Letter} sind nur noch {Fmt.Bytes(sys.Free)} frei ({freePct:0} %).";
            if (freePct < 5 || sys.Free < 5 * GB)
                findings.Add(NavigateFinding(Severity.Critical, "Speicherplatz", "Systemlaufwerk ist fast voll", detail, "Windows braucht freien Platz für Updates, Auslagerung und Caches. Nutze die Bereinigung – und die Speicheranalyse für grosse und doppelte Dateien.", "Zur Bereinigung", "cleaner"));
            else if (freePct < 12 || sys.Free < 15 * GB)
                findings.Add(NavigateFinding(Severity.Warning, "Speicherplatz", "Wenig Speicherplatz auf dem Systemlaufwerk", detail, "Mindestens 15–20 % frei halten. Die Bereinigung schafft schnell Platz.", "Zur Bereinigung", "cleaner"));
        }

        if (info.TempSize > 2 * GB)
            findings.Add(NavigateFinding(Severity.Info, "Speicherplatz", "Viele temporäre Dateien", $"Der Temp-Ordner belegt {Fmt.Bytes(info.TempSize)}.", "Temporäre Dateien können gefahrlos gelöscht werden.", "Bereinigen", "cleaner"));

        // ---------- Grafikkarte ----------
        if (avgGpu is double gpu && gpu >= 90)
        {
            var g = culprits.OrderByDescending(c => c.AvgGpu).FirstOrDefault();
            findings.Add(new Finding { Severity = Severity.Warning, Category = "Grafikkarte", Title = "Grafikkarte am Limit", Detail = $"Ø {gpu:0} % GPU-Auslastung{(g != null ? $", hauptsächlich durch {g.DisplayName}" : "")}.", Recommendation = "In Spielen: Grafikdetails/Auflösung senken oder ein FPS-Limit setzen. Ausserhalb von Spielen: Hardwarebeschleunigung in Browsern/Apps prüfen." });
        }

        // ---------- Treiber / Latenz ----------
        if (avgDpc is double dpc && avgInt is double irq)
        {
            double sum = dpc + irq;
            if (sum >= 10)
                findings.Add(new Finding { Severity = Severity.Critical, Category = "Treiber", Title = "Sehr hohe Treiber-Latenz (DPC/Interrupts)", Detail = $"{sum:0.0} % der CPU-Zeit gehen an Treiber-Interrupts. Das verursacht Mikroruckler, Audio-Knacksen und Eingabeverzögerung.", Recommendation = "Grafik-, Netzwerk-, WLAN-, Audio- und Chipsatztreiber aktualisieren. Mit „LatencyMon“ lässt sich der schuldige Treiber genau finden.", ActionLabel = "LatencyMon öffnen", Action = () => { ProcessActions.OpenUrl("https://www.resplendence.com/latencymon"); return Task.FromResult<string?>(null); } });
            else if (sum >= 4)
                findings.Add(new Finding { Severity = Severity.Warning, Category = "Treiber", Title = "Erhöhte Treiber-Latenz (DPC/Interrupts)", Detail = $"{sum:0.0} % der CPU-Zeit werden für Treiber-Interrupts verwendet (normal: unter 2 %).", Recommendation = "Treiber aktualisieren – besonders Grafik, Netzwerk/WLAN und Audio." });
        }

        // ---------- Energie ----------
        if (info.ActivePlan == PowerPlans.PowerSaver)
            findings.Add(new Finding
            {
                Severity = Severity.Warning, Category = "Energie", Title = "Energiesparplan ist aktiv",
                Detail = $"Aktiver Plan: „{info.ActivePlanName}“. Der Prozessor wird dadurch stark gedrosselt.",
                Recommendation = "Wechsle auf „Ausbalanciert“ oder „Höchstleistung“.",
                ActionLabel = "Auf Ausbalanciert wechseln",
                Action = () => Task.Run<string?>(() => PowerPlans.SetActive(PowerPlans.Balanced) ? "Umgestellt ✓" : "Fehlgeschlagen"),
            });
        if (info.OnBattery)
            findings.Add(new Finding { Severity = Severity.Info, Category = "Energie", Title = "Gerät läuft im Akkubetrieb", Detail = "Im Akkubetrieb drosselt Windows die Leistung, um Strom zu sparen.", Recommendation = "Für volle Leistung das Netzteil anschliessen." });

        // ---------- System ----------
        if (info.Uptime.TotalDays >= 7)
            findings.Add(new Finding { Severity = Severity.Warning, Category = "System", Title = "PC wurde lange nicht neu gestartet", Detail = $"Laufzeit seit dem letzten Neustart: {Fmt.Uptime(info.Uptime)}. Mit der Zeit sammeln sich Speicherlecks und hängende Prozesse an.", Recommendation = "Starte den PC neu (nicht nur Herunterfahren – Schnellstart behält den Kernel-Zustand bei)." });
        else if (info.Uptime.TotalDays >= 3)
            findings.Add(new Finding { Severity = Severity.Info, Category = "System", Title = "Neustart empfohlen", Detail = $"Laufzeit: {Fmt.Uptime(info.Uptime)}.", Recommendation = "Ein Neustart räumt Arbeitsspeicher und Hintergrundprozesse auf." });

        if (info.StartupEnabled > 12)
            findings.Add(NavigateFinding(Severity.Warning, "Autostart", "Sehr viele Autostart-Programme", $"{info.StartupEnabled} Programme starten automatisch mit Windows. Das verlangsamt den Systemstart und belegt dauerhaft Ressourcen.", "Deaktiviere Programme, die du nicht sofort nach dem Start brauchst.", "Autostart verwalten", "startup"));
        else if (info.StartupEnabled > 7)
            findings.Add(NavigateFinding(Severity.Info, "Autostart", "Einige Autostart-Programme", $"{info.StartupEnabled} Programme starten automatisch mit Windows.", "Prüfe, ob alle davon wirklich nötig sind.", "Autostart verwalten", "startup"));

        try
        {
            var unnecessary = BackgroundAnalyzer.Analyze(samples).Where(a => a.Verdict == BgVerdict.Unnecessary).ToList();
            if (unnecessary.Count >= 2)
            {
                long ram = unnecessary.Sum(a => a.Ram);
                var names = string.Join(", ", unnecessary.Take(4).Select(a => a.Name)) + (unnecessary.Count > 4 ? " …" : "");
                findings.Add(NavigateFinding(unnecessary.Count >= 5 || ram > 1.5 * GB ? Severity.Warning : Severity.Info, "Hintergrund",
                    $"{unnecessary.Count} unnötige Hintergrundprogramme",
                    $"Laufen unsichtbar und belegen zusammen {Fmt.Bytes(ram)} RAM: {names}.",
                    "Beende Updater, Launcher und Browser-Reste, die du gerade nicht brauchst.", "Hintergrund aufräumen", "background"));
            }
        }
        catch { }

        if (last.Processes.Count > 330)
            findings.Add(new Finding { Severity = Severity.Info, Category = "System", Title = "Viele Hintergrundprozesse", Detail = $"{last.Processes.Count} Prozesse laufen gerade.", Recommendation = "Nicht benötigte Programme und Autostart-Einträge reduzieren." });

        if (avgNet > 15 * MB)
            findings.Add(new Finding { Severity = Severity.Info, Category = "Netzwerk", Title = "Hoher Netzwerkverkehr", Detail = $"Ø {Fmt.Rate(avgNet)}. Downloads, Updates oder Cloud-Synchronisierung können in Online-Spielen zu hohem Ping führen.", Recommendation = "Downloads pausieren, während du spielst oder Videoanrufe führst." });

        // ---------- Score ----------
        findings = findings.OrderByDescending(f => f.Severity).ToList();
        int score = 100;
        foreach (var f in findings)
            score -= f.Severity switch { Severity.Critical => 18, Severity.Warning => 8, Severity.Info => 2, _ => 0 };
        score = Math.Clamp(score, 0, 100);

        string rating = score >= 85 ? "Sehr gut" : score >= 65 ? "Gut" : score >= 40 ? "Mässig" : "Kritisch";
        int problems = findings.Count(f => f.Severity >= Severity.Warning);
        var mainCulprit = culprits.FirstOrDefault(c => c.Impact >= 10);

        if (findings.Count == 0)
            findings.Add(new Finding { Severity = Severity.Ok, Category = "Ergebnis", Title = "Keine Probleme gefunden", Detail = "Während der Messung lief dein System rund. Treten Lags nur in bestimmten Situationen auf, starte den Scan genau dann (z. B. während eines Spiels).", Recommendation = "Tipp: Mit „Gründlich (40 s)“ werden auch seltene Spitzen erkannt." });

        string headline = problems switch
        {
            0 when findings.All(f => f.Severity <= Severity.Info) => "Dein System läuft rund",
            1 => "1 Problem gefunden",
            _ => $"{problems} Probleme gefunden",
        };
        string summary = $"Gemessen über {seconds} s ({samples.Count} Messungen). Ø CPU {avgCpu:0} %, RAM {avgRam:0} %"
                         + (avgDisk is double ad ? $", Datenträger {ad:0} %" : "")
                         + (avgGpu is double ag ? $", GPU {ag:0} %" : "") + "."
                         + (mainCulprit != null ? $" Grösster Verursacher: {mainCulprit.DisplayName}." : "");

        return new ScanResult
        {
            Time = DateTime.Now,
            DurationSec = seconds,
            Samples = samples.Count,
            Score = score,
            Rating = rating,
            Headline = headline,
            Summary = summary,
            Findings = findings,
            Culprits = culprits.Take(10).ToList(),
            AvgCpu = avgCpu,
            PeakCpu = peakCpu,
            AvgRam = avgRam,
            AvgDisk = avgDisk,
            AvgGpu = avgGpu,
            AvgDpc = avgDpc,
        };
    }
}

public sealed record ScanHistoryEntry(DateTime Time, int Score, string Rating, int Problems, string TopIssue)
{
    public string TimeText => Time.ToString("dd.MM.yyyy  HH:mm");
}

public static class ScanHistory
{
    private static string FilePath => Path.Combine(AppSettings.DataDirectory, "scan-history.json");

    public static List<ScanHistoryEntry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<ScanHistoryEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public static List<ScanHistoryEntry> Add(ScanResult r)
    {
        var list = Load();
        var top = r.Findings.FirstOrDefault(f => f.Severity >= Severity.Warning)?.Title ?? "Keine Probleme";
        list.Insert(0, new ScanHistoryEntry(r.Time, r.Score, r.Rating, r.Findings.Count(f => f.Severity >= Severity.Warning), top));
        if (list.Count > 30) list.RemoveRange(30, list.Count - 30);
        try
        {
            Directory.CreateDirectory(AppSettings.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
        }
        catch { }
        return list;
    }
}
