using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Media;

namespace PerformanceMonitor.Core;

public sealed class FileItem : ObservableObject
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastUsed { get; init; }
    public string Category { get; init; } = "";
    public string? Hint { get; set; }
    public ImageSource? Icon { get; init; }

    // Nur für Duplikate
    public string GroupKey { get; set; } = "";
    public string GroupTitle { get; set; } = "";
    public bool IsSuggestedKeep { get; set; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetField(ref _isSelected, value); }

    public string SizeText => Fmt.Bytes(Size);
    public string LastUsedText => Fmt.Ago(LastUsed);
    public bool IsRarelyUsed => LastUsed < DateTime.Now.AddDays(-FileScanner.RarelyUsedDays);
    public bool HasHint => !string.IsNullOrEmpty(Hint);
}

public readonly record struct FileScanProgress(string Message, double Fraction);

/// <summary>Sucht grosse und doppelte Dateien. Systemordner und reine Cloud-Platzhalter (z. B. OneDrive „nur online“) werden übersprungen.</summary>
public static class FileScanner
{
    public const int RarelyUsedDays = 90;
    private const int PartialBytes = 64 * 1024;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    // Programm-, Paket- und Build-Ordner: dort liegen absichtliche Kopien, deren Löschen Programme beschädigt
    private static readonly HashSet<string> ExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "packages", "venv", "site-packages", "__pycache__", "steamapps", "WindowsApps",
        "Program Files", "Program Files (x86)", "vendor", "target", "build", "dist", "out",
    };

    // Programmbibliotheken und Systemdateien nie anbieten
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".sys", ".pdb", ".lib", ".so", ".pyd", ".node", ".pak", ".dat", ".db", ".ldb", ".jar", ".asar", ".mui", ".cat", ".ttf", ".otf",
    };

    private static bool IsExcludedDirectory(DirectoryInfo d) =>
        d.Name.StartsWith('.') || ExcludedDirNames.Contains(d.Name);

    private static HashSet<string> ExcludedPaths()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrEmpty(p)) set.Add(System.IO.Path.TrimEndingDirectorySeparator(p)); }
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"));
        foreach (var d in DriveInfo.GetDrives())
        {
            Add(System.IO.Path.Combine(d.Name, "$Recycle.Bin"));
            Add(System.IO.Path.Combine(d.Name, "System Volume Information"));
            Add(System.IO.Path.Combine(d.Name, "Recovery"));
            Add(System.IO.Path.Combine(d.Name, "$WinREAgent"));
        }
        return set;
    }

    public static IEnumerable<FileInfo> EnumerateFiles(IEnumerable<string> roots, CancellationToken ct)
    {
        var excluded = ExcludedPaths();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirectoryInfo>();
        foreach (var r in roots.Where(Directory.Exists).Reverse())
            stack.Push(new DirectoryInfo(r));

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", Options).ToList(); }
            catch { continue; }

            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (excluded.Contains(d.FullName) || IsExcludedDirectory(d)) continue;
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        // Junctions/Symlinks überspringen (Endlosschleifen), Cloud-Ordner aber durchsuchen
                        try { if (d.LinkTarget != null) continue; } catch { continue; }
                    }
                    stack.Push(d);
                }
                else if (e is FileInfo f)
                {
                    var a = f.Attributes;
                    if ((a & (FileAttributes.System | FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) continue;
                    if (ExcludedExtensions.Contains(f.Extension)) continue;
                    if (seen.Add(f.FullName)) yield return f;
                }
            }
        }
    }

    // ---------- Grosse Dateien ----------

    public static List<FileItem> FindLargeFiles(IReadOnlyList<string> roots, long minSize, IProgress<FileScanProgress> progress, CancellationToken ct)
    {
        var result = new List<FileItem>();
        int scanned = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in EnumerateFiles(roots, ct))
        {
            scanned++;
            long len;
            try { len = f.Length; } catch { continue; }
            if (len >= minSize) result.Add(CreateItem(f, len, withHint: true));
            if (sw.ElapsedMilliseconds > 150)
            {
                progress.Report(new FileScanProgress($"{scanned:N0} Dateien durchsucht · {result.Count} grosse gefunden · {f.DirectoryName}", -1));
                sw.Restart();
            }
        }
        progress.Report(new FileScanProgress($"{scanned:N0} Dateien durchsucht", 1));
        return result.OrderByDescending(i => i.Size).ToList();
    }

    // ---------- Duplikate ----------

    public static List<FileItem> FindDuplicates(IReadOnlyList<string> roots, long minSize, IProgress<FileScanProgress> progress, CancellationToken ct)
    {
        // 1) Nach Grösse gruppieren – nur gleich grosse Dateien können identisch sein
        var bySize = new Dictionary<long, List<FileInfo>>();
        int scanned = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in EnumerateFiles(roots, ct))
        {
            scanned++;
            long len;
            try { len = f.Length; } catch { continue; }
            if (len < minSize) continue;
            if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<FileInfo>(2);
            list.Add(f);
            if (sw.ElapsedMilliseconds > 150)
            {
                progress.Report(new FileScanProgress($"Schritt 1/2: {scanned:N0} Dateien durchsucht · {f.DirectoryName}", -1));
                sw.Restart();
            }
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key).ToList();
        int candidateFiles = candidates.Sum(c => c.Value.Count);

        // 2) Teil-Hash (Anfang + Ende), danach vollständiger SHA-256 nur bei Übereinstimmung
        var groups = new List<List<FileInfo>>();
        int processed = 0;
        foreach (var (size, files) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new FileScanProgress($"Schritt 2/2: Vergleiche Inhalte ({processed:N0} / {candidateFiles:N0} Dateien) · {files[0].Name}",
                candidateFiles == 0 ? 1 : (double)processed / candidateFiles));
            processed += files.Count;

            var partialGroups = files.Select(f => (File: f, Hash: PartialHash(f.FullName, size)))
                .Where(x => x.Hash != null)
                .GroupBy(x => x.Hash!)
                .Where(g => g.Count() > 1);

            foreach (var pg in partialGroups)
            {
                if (size <= PartialBytes * 2)
                {
                    groups.Add(pg.Select(x => x.File).ToList());
                    continue;
                }
                foreach (var fg in pg.Select(x => (x.File, Hash: FullHash(x.File.FullName, ct)))
                             .Where(x => x.Hash != null)
                             .GroupBy(x => x.Hash!)
                             .Where(g => g.Count() > 1))
                    groups.Add(fg.Select(x => x.File).ToList());
            }
        }

        // 3) Ergebnis aufbereiten: Gruppen nach verschwendetem Platz sortieren
        var result = new List<FileItem>();
        int groupNo = 0;
        // Hardlinks zeigen auf dieselben Daten – Löschen würde keinen Platz sparen, also nur echte Kopien behalten
        groups = groups
            .Select(g => g.GroupBy(f => GetFileId(f.FullName) ?? f.FullName).Select(x => x.First()).ToList())
            .Where(g => g.Count > 1)
            .ToList();

        foreach (var g in groups.OrderByDescending(g => g[0].Length * (g.Count - 1)))
        {
            groupNo++;
            long size = g[0].Length;
            var items = g.Select(f => CreateItem(f, size, withHint: false)).ToList();
            var keep = items.OrderBy(KeepPenalty).ThenBy(i => i.LastUsed).First();
            string title = $"{g.Count} identische Dateien  ·  je {Fmt.Bytes(size)}  ·  {Fmt.Bytes(size * (g.Count - 1))} verschwendet";
            foreach (var item in items.OrderByDescending(i => ReferenceEquals(i, keep)).ThenBy(i => i.Path))
            {
                item.GroupKey = $"{groupNo:D5}";
                item.GroupTitle = title;
                item.IsSuggestedKeep = ReferenceEquals(item, keep);
                item.Hint = item.IsSuggestedKeep ? "Vorschlag: behalten" : null;
                result.Add(item);
            }
        }
        progress.Report(new FileScanProgress($"{scanned:N0} Dateien durchsucht", 1));
        return result;
    }

    /// <summary>Je kleiner, desto eher ist die Datei das „Original“.</summary>
    private static int KeepPenalty(FileItem i)
    {
        int p = 0;
        var name = System.IO.Path.GetFileNameWithoutExtension(i.Name);
        if (name.Contains("Kopie", StringComparison.OrdinalIgnoreCase) || name.Contains("Copy", StringComparison.OrdinalIgnoreCase)) p += 3;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\(\d+\)$")) p += 3;
        if (i.Path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)) p += 2;
        if (i.Path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)) p += 2;
        return p;
    }

    private static FileStream OpenRead(string path, int buffer) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer, FileOptions.SequentialScan);

    private static string? PartialHash(string path, long size)
    {
        try
        {
            using var fs = OpenRead(path, PartialBytes);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[PartialBytes];
            int read = fs.ReadAtLeast(buffer, (int)Math.Min(PartialBytes, size), throwOnEndOfStream: false);
            hash.AppendData(buffer, 0, read);
            if (size > PartialBytes * 2)
            {
                fs.Seek(-PartialBytes, SeekOrigin.End);
                read = fs.ReadAtLeast(buffer, PartialBytes, throwOnEndOfStream: false);
                hash.AppendData(buffer, 0, read);
            }
            else if (size > PartialBytes)
            {
                read = fs.ReadAtLeast(buffer, (int)(size - PartialBytes), throwOnEndOfStream: false);
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch { return null; }
    }

    private static string? FullHash(string path, CancellationToken ct)
    {
        try
        {
            using var fs = OpenRead(path, 1 << 20);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ---------- Einordnung ----------

    private static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "Video", [".mkv"] = "Video", [".avi"] = "Video", [".mov"] = "Video", [".wmv"] = "Video", [".m4v"] = "Video", [".webm"] = "Video", [".mts"] = "Video",
        [".zip"] = "Archiv", [".rar"] = "Archiv", [".7z"] = "Archiv", [".tar"] = "Archiv", [".gz"] = "Archiv", [".tgz"] = "Archiv",
        [".exe"] = "Installation", [".msi"] = "Installation", [".msix"] = "Installation", [".appx"] = "Installation", [".msixbundle"] = "Installation",
        [".iso"] = "Disk-Image", [".img"] = "Disk-Image", [".vhd"] = "Disk-Image", [".vhdx"] = "Disk-Image", [".vmdk"] = "Disk-Image", [".vdi"] = "Disk-Image",
        [".mp3"] = "Audio", [".wav"] = "Audio", [".flac"] = "Audio", [".m4a"] = "Audio", [".aac"] = "Audio",
        [".jpg"] = "Bild", [".jpeg"] = "Bild", [".png"] = "Bild", [".heic"] = "Bild", [".raw"] = "Bild", [".cr2"] = "Bild", [".nef"] = "Bild", [".arw"] = "Bild", [".psd"] = "Bild", [".tif"] = "Bild", [".tiff"] = "Bild",
        [".pdf"] = "Dokument", [".docx"] = "Dokument", [".pptx"] = "Dokument", [".xlsx"] = "Dokument",
        [".bak"] = "Backup", [".old"] = "Backup", [".backup"] = "Backup",
        [".log"] = "Protokoll", [".dmp"] = "Protokoll", [".etl"] = "Protokoll",
        [".pst"] = "E-Mail-Archiv", [".ost"] = "E-Mail-Archiv",
    };

    private static FileItem CreateItem(FileInfo f, long size, bool withHint)
    {
        // Bewusst ohne LastAccessTime: Virenscanner, Indexer und das Auslesen von Icons setzen sie ständig auf „jetzt“.
        // Änderungs- bzw. Erstellungsdatum (= Download/Kopie) ist verlässlicher.
        DateTime lastUsed = f.LastWriteTime;
        try { if (f.CreationTime > lastUsed && f.CreationTime <= DateTime.Now) lastUsed = f.CreationTime; } catch { }
        var category = Categories.GetValueOrDefault(f.Extension, "Sonstige");
        var item = new FileItem
        {
            Path = f.FullName,
            Name = f.Name,
            Folder = f.DirectoryName ?? "",
            Size = size,
            LastUsed = lastUsed,
            Category = category,
            Icon = IconCache.GetForFile(f.FullName),
        };
        if (withHint) item.Hint = BuildHint(item);
        return item;
    }

    private static string? BuildHint(FileItem i)
    {
        bool inDownloads = i.Path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);
        string? hint = i.Category switch
        {
            "Installation" when inDownloads => "Installationsdatei – nach der Installation meist überflüssig.",
            "Installation" => null,
            "Archiv" => "Archive werden nach dem Entpacken oft nicht mehr gebraucht.",
            "Disk-Image" => "Abbilder (ISO/VM) sind sehr gross und werden selten benötigt.",
            "Backup" => "Altes Backup – prüfen, ob es noch gebraucht wird.",
            "Protokoll" => "Log-/Dump-Datei – kann meist gelöscht werden.",
            "Video" => "Videos gehören zu den grössten Platzfressern. Ggf. auf eine externe Festplatte oder in die Cloud verschieben.",
            "E-Mail-Archiv" => "Outlook-Datendatei – nicht löschen, solange Outlook sie verwendet.",
            _ => null,
        };
        if (i.IsRarelyUsed)
        {
            var unused = $"Seit {Fmt.Ago(i.LastUsed).Replace("vor ", "")} unverändert.";
            hint = hint == null ? unused : $"{unused} {hint}";
        }
        else if (inDownloads && hint == null)
            hint = "Liegt im Download-Ordner.";
        return hint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    /// <summary>Eindeutige Kennung der Datei auf dem Datenträger (gleich bei Hardlinks).</summary>
    private static string? GetFileId(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(fs.SafeFileHandle, out var info)) return null;
            return $"{info.VolumeSerialNumber:X}-{info.FileIndexHigh:X}-{info.FileIndexLow:X}";
        }
        catch { return null; }
    }

    // ---------- Löschen (Papierkorb) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Verschiebt Dateien in den Papierkorb. Liefert die Pfade, die anschliessend nicht mehr existieren.</summary>
    public static List<string> MoveToRecycleBin(IReadOnlyList<string> paths, IntPtr owner)
    {
        if (paths.Count == 0) return new();
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_WANTNUKEWARNING,
        };
        SHFileOperation(ref op);
        return paths.Where(p => !File.Exists(p)).ToList();
    }
}
