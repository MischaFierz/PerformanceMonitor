using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PerformanceMonitor.Core;

public sealed record CleanTarget(string Folder, string Pattern = "*", bool Recursive = true);

public sealed class CleanupCategory : ObservableObject
{
    public string Group { get; init; } = "Windows";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Glyph { get; init; } = "";
    public bool RequiresAdmin { get; init; }
    public bool IsRecycleBin { get; init; }
    public bool Recommended { get; init; }
    public int MinAgeHours { get; init; }
    public string[] ProcessNames { get; init; } = [];
    public List<CleanTarget> Targets { get; } = new();

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    private long _size = -1;
    public long Size
    {
        get => _size;
        set { if (SetField(ref _size, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private int _fileCount;
    public int FileCount
    {
        get => _fileCount;
        set { if (SetField(ref _fileCount, value)) OnPropertyChanged(nameof(CountText)); }
    }

    public string SizeText => _size < 0 ? "–" : Fmt.Bytes(_size);
    public string CountText => _size < 0 || IsRecycleBin ? "" : $"{_fileCount:N0} Dateien";

    private string _status = "";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private bool _isAppRunning;
    public bool IsAppRunning { get => _isAppRunning; set => SetField(ref _isAppRunning, value); }

    public bool AdminMissing => RequiresAdmin && !App.IsAdmin;

    public bool CheckRunning()
    {
        IsAppRunning = ProcessNames.Any(n =>
        {
            var procs = Process.GetProcessesByName(n);
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        });
        return IsAppRunning;
    }
}

public static class CleanupService
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions TopOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // Typische Cache-Unterordner von Chromium-Browsern und Electron-Apps
    private static readonly string[] ChromiumCacheDirs =
    [
        @"Cache\Cache_Data", "Code Cache", "GPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache",
        "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "Cache", "CachedData",
    ];

    public static List<CleanupCategory> CreateCategories()
    {
        var list = new List<CleanupCategory>();

        // ===================== Windows =====================
        list.Add(Cat("Windows", "Temporäre Dateien (Benutzer)", "Von Programmen und Installationen zurückgelassene Dateien.", "",
            recommended: true, minAge: 2, targets: [new(Path.GetTempPath())]));
        list.Add(Cat("Windows", "Temporäre Dateien (Windows)", "Temporäre Systemdateien im Windows-Ordner.", "",
            recommended: true, admin: true, minAge: 2, targets: [new(Path.Combine(Windows, "Temp"))]));
        list.Add(Cat("Windows", "Internet-Cache von Windows", "Zwischenspeicher von Windows-Komponenten, Widgets und älteren Apps (INetCache).", "",
            recommended: true, targets: [new(Path.Combine(Local, @"Microsoft\Windows\INetCache"))]));
        list.Add(Cat("Windows", "Miniaturansichten-Cache", "Vorschaubilder des Explorers. Werden bei Bedarf automatisch neu erstellt.", "",
            recommended: true, targets: [
                new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", false),
                new(Path.Combine(Local, @"Microsoft\Windows\Explorer"), "iconcache_*.db", false)]));
        list.Add(Cat("Windows", "Fehlerberichte & Absturzabbilder", "Windows-Fehlerberichte (WER) und Crash-Dumps von Programmen.", "",
            recommended: true, targets: [
                new(Path.Combine(Local, @"Microsoft\Windows\WER")),
                new(Path.Combine(ProgramData, @"Microsoft\Windows\WER")),
                new(Path.Combine(Local, "CrashDumps"))]));
        list.Add(Cat("Windows", "System-Speicherabbilder", "Abbilder nach Bluescreens (MEMORY.DMP, Minidumps). Oft mehrere GB gross.", "",
            admin: true, targets: [
                new(Windows, "MEMORY.DMP", false),
                new(Path.Combine(Windows, "Minidump")),
                new(Path.Combine(Windows, "LiveKernelReports"), "*.dmp")]));
        list.Add(Cat("Windows", "Windows Update-Downloads", "Bereits installierte Update-Pakete. Windows lädt sie bei Bedarf erneut herunter.", "",
            admin: true, targets: [new(Path.Combine(Windows, @"SoftwareDistribution\Download"))]));
        list.Add(Cat("Windows", "Übermittlungsoptimierung", "Update-Dateien, die Windows mit anderen PCs teilt.", "",
            admin: true, targets: [new(Path.Combine(Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))]));
        list.Add(Cat("Windows", "Windows-Protokolldateien", "Alte Protokolle von Setup und Systemdiensten (älter als 1 Tag).", "",
            admin: true, minAge: 24, targets: [new(Path.Combine(Windows, "Logs"), "*.log"), new(Path.Combine(Windows, "Logs"), "*.etl"), new(Path.Combine(Windows, "Panther"), "*.log")]));
        list.Add(Cat("Windows", "DirectX-Shader-Cache", "Vorkompilierte Grafik-Shader. Werden neu erstellt – Spiele laden beim ersten Start danach etwas länger.", "",
            targets: [new(Path.Combine(Local, "D3DSCache"))]));
        list.Add(new CleanupCategory
        {
            Group = "Windows", Name = "Papierkorb", Description = "Endgültiges Löschen aller Dateien im Papierkorb (alle Laufwerke).",
            Glyph = "", IsRecycleBin = true,
        });

        // ===================== Browser =====================
        AddBrowser(list, "Google Chrome", ["chrome"], Path.Combine(Local, @"Google\Chrome\User Data"));
        AddBrowser(list, "Microsoft Edge", ["msedge"], Path.Combine(Local, @"Microsoft\Edge\User Data"));
        AddBrowser(list, "Brave", ["brave"], Path.Combine(Local, @"BraveSoftware\Brave-Browser\User Data"));
        AddBrowser(list, "Vivaldi", ["vivaldi"], Path.Combine(Local, @"Vivaldi\User Data"));
        AddApp(list, "Browser", "Opera", ["opera"], "", "Zwischenspeicher von Opera. Anmeldungen und Lesezeichen bleiben erhalten.",
            Path.Combine(Local, @"Opera Software\Opera Stable"), Path.Combine(Roaming, @"Opera Software\Opera Stable"));
        AddApp(list, "Browser", "Opera GX", ["opera"], "", "Zwischenspeicher von Opera GX. Anmeldungen und Lesezeichen bleiben erhalten.",
            Path.Combine(Local, @"Opera Software\Opera GX Stable"), Path.Combine(Roaming, @"Opera Software\Opera GX Stable"));
        var firefox = Path.Combine(Local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(firefox))
        {
            var ff = Cat("Browser", "Mozilla Firefox", "Zwischenspeicher von Firefox. Anmeldungen, Verlauf und Lesezeichen bleiben erhalten.", "",
                recommended: true, processes: ["firefox"], targets: []);
            foreach (var p in SafeDirectories(firefox))
            {
                ff.Targets.Add(new(Path.Combine(p, "cache2")));
                ff.Targets.Add(new(Path.Combine(p, "startupCache")));
                ff.Targets.Add(new(Path.Combine(p, "thumbnails")));
            }
            list.Add(ff);
        }

        // ===================== Apps & Spiele =====================
        AddApp(list, "Apps & Spiele", "Discord", ["Discord"], "", "Bilder-, Video- und Code-Cache von Discord.", Path.Combine(Roaming, "discord"));
        AddApp(list, "Apps & Spiele", "Microsoft Teams", ["ms-teams", "Teams"], "", "Cache von Teams (klassisch und neu). Anmeldung bleibt erhalten.",
            Path.Combine(Roaming, @"Microsoft\Teams"),
            Path.Combine(Local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default"),
            Path.Combine(Local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView"));
        AddApp(list, "Apps & Spiele", "Slack", ["slack"], "", "Cache von Slack.", Path.Combine(Roaming, "Slack"));
        AddFolders(list, "Apps & Spiele", "Spotify", ["Spotify"], "", "Zwischengespeicherte Songs und Cover. Offline-Downloads bleiben erhalten.",
            Path.Combine(Local, @"Spotify\Storage"), Path.Combine(Local, @"Spotify\Data"));
        AddFolders(list, "Apps & Spiele", "Steam (Browser-Cache)", ["steam", "steamwebhelper"], "", "Cache der Steam-Oberfläche (Shop, Community). Spiele bleiben unberührt.",
            Path.Combine(Local, @"Steam\htmlcache"));
        AddFolders(list, "Apps & Spiele", "Epic Games Launcher", ["EpicGamesLauncher"], "", "Web-Cache des Epic Games Launchers.",
            Path.Combine(Local, @"EpicGamesLauncher\Saved\webcache"), Path.Combine(Local, @"EpicGamesLauncher\Saved\webcache_4147"), Path.Combine(Local, @"EpicGamesLauncher\Saved\webcache_4430"));
        AddApp(list, "Apps & Spiele", "Visual Studio Code", ["Code"], "", "Caches von VS Code. Erweiterungen und Einstellungen bleiben erhalten.",
            Path.Combine(Roaming, "Code"));
        AddFolders(list, "Apps & Spiele", "NVIDIA Shader-Cache", [], "", "Shader-Cache des NVIDIA-Treibers. Wird neu aufgebaut (erste Spielstarts etwas langsamer).",
            Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"), Path.Combine(Local, @"NVIDIA Corporation\NV_Cache"), Path.Combine(ProgramData, @"NVIDIA Corporation\NV_Cache"));
        AddFolders(list, "Apps & Spiele", "AMD Shader-Cache", [], "", "Shader-Cache des AMD-Treibers. Wird neu aufgebaut (erste Spielstarts etwas langsamer).",
            Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"), Path.Combine(Local, @"AMD\VkCache"), Path.Combine(Local, @"AMD\GLCache"));

        // ===================== Entwicklung =====================
        AddFolders(list, "Entwicklung", "NuGet-HTTP-Cache", [], "", "Heruntergeladene Paket-Metadaten. Installierte Pakete bleiben erhalten.",
            Path.Combine(Local, @"NuGet\v3-cache"), Path.Combine(Local, @"NuGet\plugins-cache"));
        AddFolders(list, "Entwicklung", "npm-Cache", [], "", "Zwischenspeicher von npm. Wird beim nächsten „npm install“ neu gefüllt.",
            Path.Combine(Local, "npm-cache"), Path.Combine(Roaming, "npm-cache"));
        AddFolders(list, "Entwicklung", "Yarn-Cache", [], "", "Zwischenspeicher von Yarn.", Path.Combine(Local, @"Yarn\Cache"));
        AddFolders(list, "Entwicklung", "pip-Cache", [], "", "Zwischenspeicher von Python pip.", Path.Combine(Local, @"pip\cache"));
        AddFolders(list, "Entwicklung", "Gradle-Cache", ["java"], "", "Heruntergeladene Gradle-Abhängigkeiten. Der nächste Build lädt sie erneut.",
            Path.Combine(Profile, @".gradle\caches"));

        return list;
    }

    private static CleanupCategory Cat(string group, string name, string desc, string glyph, List<CleanTarget> targets,
        bool recommended = false, bool admin = false, int minAge = 0, string[]? processes = null)
    {
        var c = new CleanupCategory
        {
            Group = group, Name = name, Description = desc, Glyph = glyph, Recommended = recommended,
            RequiresAdmin = admin, MinAgeHours = minAge, ProcessNames = processes ?? [],
            IsSelected = recommended && (!admin || App.IsAdmin),
        };
        c.Targets.AddRange(targets);
        return c;
    }

    /// <summary>Chromium-Browser: alle Profile (Default, Profile 1 …) plus profilübergreifende Shader-Caches.</summary>
    private static void AddBrowser(List<CleanupCategory> list, string name, string[] processes, string userData)
    {
        if (!Directory.Exists(userData)) return;
        var c = Cat("Browser", name, $"Zwischenspeicher von {name} (alle Profile). Anmeldungen, Verlauf und Lesezeichen bleiben erhalten.", "",
            recommended: true, processes: processes, targets: []);
        foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
            AddIfExists(c, Path.Combine(userData, shared));
        foreach (var profile in SafeDirectories(userData))
        {
            var n = Path.GetFileName(profile);
            if (n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile")
                AddChromiumDirs(c, profile);
        }
        if (c.Targets.Count > 0) list.Add(c);
    }

    /// <summary>Electron-App oder Browser mit einem einzigen Profilordner.</summary>
    private static void AddApp(List<CleanupCategory> list, string group, string name, string[] processes, string glyph, string desc, params string[] roots)
    {
        var c = Cat(group, name, desc, glyph, processes: processes, targets: []);
        foreach (var r in roots.Where(Directory.Exists)) AddChromiumDirs(c, r);
        if (c.Targets.Count > 0) list.Add(c);
    }

    private static void AddFolders(List<CleanupCategory> list, string group, string name, string[] processes, string glyph, string desc, params string[] folders)
    {
        var c = Cat(group, name, desc, glyph, processes: processes, targets: []);
        foreach (var f in folders) AddIfExists(c, f);
        if (c.Targets.Count > 0) list.Add(c);
    }

    private static void AddChromiumDirs(CleanupCategory c, string root)
    {
        foreach (var sub in ChromiumCacheDirs) AddIfExists(c, Path.Combine(root, sub));
    }

    private static void AddIfExists(CleanupCategory c, string folder)
    {
        if (!Directory.Exists(folder)) return;
        // Überschneidungen vermeiden (z. B. "Cache" und "Cache\Cache_Data")
        if (c.Targets.Any(t => folder.StartsWith(t.Folder + "\\", StringComparison.OrdinalIgnoreCase))) return;
        c.Targets.RemoveAll(t => t.Folder.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase));
        c.Targets.Add(new CleanTarget(folder));
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    public static (long Size, int Files) Measure(CleanupCategory category)
    {
        if (category.IsRecycleBin)
        {
            var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Native.SHQUERYRBINFO>() };
            return Native.SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, (int)info.i64NumItems) : (0, 0);
        }
        long total = 0;
        int files = 0;
        foreach (var t in category.Targets)
        {
            var (s, n) = MeasureFolderDetailed(t.Folder, t.Pattern, t.Recursive);
            total += s;
            files += n;
        }
        return (total, files);
    }

    public static long MeasureFolder(string folder, string pattern = "*", bool recursive = true) =>
        MeasureFolderDetailed(folder, pattern, recursive).Size;

    private static (long Size, int Files) MeasureFolderDetailed(string folder, string pattern, bool recursive)
    {
        long total = 0;
        int files = 0;
        try
        {
            if (!Directory.Exists(folder)) return (0, 0);
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles(pattern, recursive ? Recursive : TopOnly))
            {
                try { total += f.Length; files++; } catch { }
            }
        }
        catch { }
        return (total, files);
    }

    /// <param name="minAgeOverride">null = Standard der Kategorie, sonst Mindestalter in Stunden (0 = alles).</param>
    public static (long Freed, int Deleted, int Skipped) Clean(CleanupCategory category, int? minAgeOverride = null, bool removeEmptyFolders = true)
    {
        if (category.IsRecycleBin)
        {
            long before = Measure(category).Size;
            const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
            int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 ? (before, 1, 0) : (0, 0, 1);
        }

        int minAge = minAgeOverride ?? category.MinAgeHours;
        long freed = 0;
        int deleted = 0, skipped = 0;
        var cutoff = DateTime.UtcNow.AddHours(-minAge);
        foreach (var t in category.Targets)
        {
            if (!Directory.Exists(t.Folder)) continue;
            List<FileInfo> files;
            try { files = new DirectoryInfo(t.Folder).EnumerateFiles(t.Pattern, t.Recursive ? Recursive : TopOnly).ToList(); }
            catch { continue; }

            foreach (var f in files)
            {
                try
                {
                    if (minAge > 0 && f.LastWriteTimeUtc > cutoff) { skipped++; continue; }
                    long len = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    freed += len;
                    deleted++;
                }
                catch { skipped++; }
            }

            if (t.Recursive && removeEmptyFolders) DeleteEmptySubdirectories(t.Folder);
        }
        return (freed, deleted, skipped);
    }

    /// <summary>Beendet die zur Kategorie gehörenden Programme (z. B. Browser), damit gesperrte Dateien gelöscht werden können.</summary>
    public static int CloseApps(CleanupCategory category)
    {
        int closed = 0;
        foreach (var name in category.ProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow() && p.WaitForExit(3000)) { closed++; continue; }
                        p.Kill();
                        p.WaitForExit(3000);
                        closed++;
                    }
                    catch { }
                }
            }
        }
        return closed;
    }

    private static void DeleteEmptySubdirectories(string root)
    {
        try
        {
            var dirs = new DirectoryInfo(root).EnumerateDirectories("*", Recursive)
                .Select(d => d.FullName)
                .OrderByDescending(d => d.Length)
                .ToList();
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
                }
                catch { }
            }
        }
        catch { }
    }
}
