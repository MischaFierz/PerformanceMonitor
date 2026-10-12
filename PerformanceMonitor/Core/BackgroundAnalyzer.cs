using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace PerformanceMonitor.Core;

public enum BgVerdict { Unnecessary = 0, Optional = 1, Important = 2 }

public sealed class BackgroundApp : ObservableObject
{
    public string Name { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public string? Path { get; init; }
    public ImageSource? Icon { get; init; }
    public string Category { get; init; } = "";
    public BgVerdict Verdict { get; init; }
    public string Hint { get; init; } = "";
    public List<int> Pids { get; init; } = new();
    public long Ram { get; init; }
    public double Cpu { get; init; }
    public StartupEntry? Startup { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    private string _status = "";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private bool _isClosed;
    public bool IsClosed { get => _isClosed; set => SetField(ref _isClosed, value); }

    private bool _startupDisabled;
    public bool StartupDisabled { get => _startupDisabled; set { if (SetField(ref _startupDisabled, value)) { OnPropertyChanged(nameof(CanDisableStartup)); OnPropertyChanged(nameof(InAutostart)); } } }

    public string VerdictText => Verdict switch
    {
        BgVerdict.Unnecessary => "Unnötig",
        BgVerdict.Optional => "Optional",
        _ => "Wichtig",
    };

    public string RamText => Fmt.Bytes(Ram);
    public string CpuText => $"{Cpu:0.0} %";
    public string CountText => Pids.Count > 1 ? $"{ProcessName} · {Pids.Count} Prozesse" : ProcessName;
    public bool InAutostart => Startup is { IsEnabled: true };
    public bool CanDisableStartup => InAutostart && !StartupDisabled;
}

/// <summary>
/// Findet Programme, die ohne sichtbares Fenster im Hintergrund laufen (Updater, Launcher, Tray-Tools, Browser-Reste …),
/// und bewertet, ob sie gebraucht werden. Systemprozesse, Dienste und Hilfsprozesse geöffneter Programme werden ausgelassen.
/// </summary>
public static class BackgroundAnalyzer
{
    private sealed record Rule(string[] Match, string Category, BgVerdict Verdict, string Hint);

    // Reihenfolge = Priorität
    private static readonly Rule[] Rules =
    [
        new(["avast", "avg", "kaspersky", "avp", "bdservicehost", "bitdefender", "norton", "mcafee", "eset", "ekrn", "egui", "sophos", "malwarebytes", "mbam"],
            "Sicherheit", BgVerdict.Important, "Antivirus/Sicherheitssoftware – sollte laufen, um dich zu schützen."),
        new(["update", "updater", "autoupdate", "adobearm", "jusched", "squirrel"],
            "Updater", BgVerdict.Unnecessary, "Sucht im Hintergrund nach Updates. Muss nicht dauernd laufen – Updates kommen auch beim nächsten Programmstart."),
        new(["crashpad", "crashhandler", "crashreport", "werfault", "bugreport", "telemetry", "diagtrack"],
            "Telemetrie", BgVerdict.Unnecessary, "Sammelt Absturz- oder Nutzungsdaten. Kann bedenkenlos beendet werden."),
        new(["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"],
            "Browser", BgVerdict.Unnecessary, "Der Browser läuft weiter, obwohl kein Fenster offen ist (Hintergrund-Apps). In den Browser-Einstellungen „Im Hintergrund weiter ausführen“ deaktivieren."),
        new(["steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "battle.net", "origin", "eadesktop", "eabackgroundservice", "ubisoftconnect", "upc", "riotclient", "galaxyclient", "rockstar", "launcher"],
            "Spiele-Launcher", BgVerdict.Unnecessary, "Spiele-Launcher – wird nur beim Spielen gebraucht. Belegt im Hintergrund RAM und Netzwerk."),
        new(["ccxprocess", "creative cloud", "adobeipcbroker", "adobecollabsync", "acrotray", "coresync", "adobe desktop service"],
            "Adobe-Hintergrund", BgVerdict.Unnecessary, "Adobe-Hintergrunddienst. Wird nur gebraucht, wenn Adobe-Programme geöffnet sind."),
        new(["onedrive", "dropbox", "googledrivefs", "icloud", "nextcloud", "megasync", "synology"],
            "Cloud-Synchronisierung", BgVerdict.Optional, "Synchronisiert deine Dateien. Nur beenden, wenn du gerade keine Synchronisierung brauchst."),
        new(["discord", "teams", "ms-teams", "slack", "skype", "whatsapp", "telegram", "zoom", "signal"],
            "Kommunikation", BgVerdict.Optional, "Messenger im Hintergrund – nötig für Benachrichtigungen. Belegt aber oft viel RAM."),
        new(["spotify", "itunes", "applemusic"],
            "Medien", BgVerdict.Optional, "Musik-App im Hintergrund."),
        new(["nvcontainer", "nvidia", "nvsphelper", "radeon", "amdrs", "amdow", "logi", "lghub", "razer", "icue", "corsair", "steelseries", "armoury", "asus", "msi", "rtkaud", "realtek", "synaptics", "elan", "dolby", "nahimic", "wacom", "hp", "lenovo", "dell", "intel"],
            "Hardware-Tool", BgVerdict.Optional, "Treiber- oder Geräte-Hilfsprogramm (z. B. für Maus, RGB, Audio oder Touchpad). Oft nötig, damit Sondertasten/Effekte funktionieren."),
        new(["phoneexperiencehost", "yourphone", "widgets", "widgetservice", "gamebar", "xboxapp", "cortana"],
            "Windows-App", BgVerdict.Unnecessary, "Optionale Windows-App im Hintergrund. Lässt sich in den Windows-Einstellungen deaktivieren."),
        new(["docker", "com.docker", "vmware", "virtualbox", "wsl"],
            "Entwicklung", BgVerdict.Optional, "Entwickler-/Virtualisierungswerkzeug – braucht im Hintergrund viel RAM. Beenden, wenn du es gerade nicht nutzt."),
    ];

    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2", "conhost", "dllhost", "ctfmon", "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "LockApp", "SecurityHealthSystray", "explorer", "PerfPulse", "RuntimeBroker", "backgroundTaskHost",
        "ApplicationFrameHost", "SystemSettings", "smartscreen", "taskhostw", "sihost", "WindowsTerminal", "OpenConsole", "cmd", "powershell", "pwsh",
    };

    public static List<BackgroundApp> Analyze(IReadOnlyList<Snapshot> samples)
    {
        var last = samples[^1];
        var windowPids = GetPidsWithVisibleWindows();
        var parents = GetParentMap();
        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        uint mySession = GetSession(Environment.ProcessId);

        // Hilfsprozesse geöffneter Programme erkennen: Vorfahre mit sichtbarem Fenster
        bool HasForegroundAncestor(int pid)
        {
            for (int depth = 0, cur = pid; depth < 6 && parents.TryGetValue(cur, out var parent) && parent > 4; depth++, cur = parent)
                if (windowPids.Contains(parent)) return true;
            return false;
        }

        // Ø-CPU je Prozess-ID über alle Messungen
        var cpuSum = new Dictionary<int, double>();
        foreach (var s in samples)
            foreach (var p in s.Processes)
                cpuSum[p.Pid] = cpuSum.GetValueOrDefault(p.Pid) + p.Cpu;

        List<StartupEntry> startup;
        try { startup = StartupManager.Load(); } catch { startup = new(); }

        var result = new List<BackgroundApp>();
        foreach (var group in last.Processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var procs = group.ToList();
            var first = procs.FirstOrDefault(p => p.Path != null) ?? procs[0];
            if (Ignore.Contains(group.Key) || KnownProcesses.IsCritical(group.Key)) continue;
            if (first.Path == null) continue; // kein Zugriff -> Dienst oder geschützter Systemprozess
            if (first.Path.StartsWith(windowsDir + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            if (procs.Any(p => windowPids.Contains(p.Pid))) continue; // hat ein sichtbares Fenster
            if (procs.All(p => HasForegroundAncestor(p.Pid))) continue; // gehört zu einem geöffneten Programm
            if (procs.All(p => GetSession(p.Pid) != mySession)) continue; // Windows-Dienst (Sitzung 0)

            var (category, verdict, hint) = Classify(group.Key, first.Description, first.Path);
            var exeName = System.IO.Path.GetFileName(first.Path);
            var entry = startup.FirstOrDefault(e => e.FilePath != null &&
                (e.FilePath.Equals(first.Path, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(e.FilePath).Equals(exeName, StringComparison.OrdinalIgnoreCase)));

            result.Add(new BackgroundApp
            {
                Name = first.Description ?? group.Key,
                ProcessName = group.Key,
                Path = first.Path,
                Icon = first.Icon,
                Category = category,
                Verdict = verdict,
                Hint = hint,
                Pids = procs.Select(p => p.Pid).ToList(),
                Ram = procs.Sum(p => p.WorkingSet),
                Cpu = procs.Sum(p => cpuSum.GetValueOrDefault(p.Pid)) / samples.Count,
                Startup = entry,
            });
        }

        return result.OrderBy(a => a.Verdict).ThenByDescending(a => a.Ram).ToList();
    }

    private static (string Category, BgVerdict Verdict, string Hint) Classify(string name, string? description, string path)
    {
        string n = name.ToLowerInvariant();
        string desc = (description ?? "").ToLowerInvariant();
        // Kurze Muster nur als Namensanfang, längere im Namen, in der Beschreibung nur als ganzes Wort
        bool Matches(string m) =>
            n.StartsWith(m) || (m.Length >= 4 && n.Contains(m)) ||
            System.Text.RegularExpressions.Regex.IsMatch(desc, $@"\b{System.Text.RegularExpressions.Regex.Escape(m)}\b");
        foreach (var rule in Rules)
            if (rule.Match.Any(Matches))
                return (rule.Category, rule.Verdict, rule.Hint);
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return ("Store-App", BgVerdict.Optional, "App aus dem Microsoft Store, die im Hintergrund läuft.");
        return ("Hintergrundprogramm", BgVerdict.Optional, "Läuft ohne sichtbares Fenster (z. B. im Infobereich). Wenn du das Programm nicht kennst oder nicht brauchst, kannst du es beenden.");
    }

    // ---------- Native Hilfen ----------

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    private static uint GetSession(int pid) => ProcessIdToSessionId((uint)pid, out var s) ? s : 0;

    /// <summary>Prozesse mit mindestens einem sichtbaren, nicht versteckten Hauptfenster.</summary>
    public static HashSet<int> GetPidsWithVisibleWindows()
    {
        var set = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
            if (GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            set.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        return set;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);

    private static Dictionary<int, int> GetParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref e))
                do map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                while (Process32NextW(snap, ref e));
        }
        finally { Native.CloseHandle(snap); }
        return map;
    }
}
