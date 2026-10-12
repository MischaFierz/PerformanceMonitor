using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Microsoft.Win32;

namespace PerformanceMonitor.Core;

public enum StartupKind { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

public sealed class StartupEntry : ObservableObject
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? FilePath { get; init; }
    public string Publisher { get; init; } = "";
    public StartupKind Kind { get; init; }
    public string ApprovedValueName { get; init; } = "";
    public string? ShortcutPath { get; init; }
    public ImageSource? Icon { get; init; }

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => SetField(ref _isEnabled, value); }

    public bool RequiresAdmin => Kind is StartupKind.MachineRun or StartupKind.MachineRun32 or StartupKind.CommonFolder;

    public string Location => Kind switch
    {
        StartupKind.UserRun => "Registry (Benutzer)",
        StartupKind.MachineRun => "Registry (alle Benutzer)",
        StartupKind.MachineRun32 => "Registry (alle Benutzer, 32-Bit)",
        StartupKind.UserFolder => "Autostart-Ordner (Benutzer)",
        _ => "Autostart-Ordner (alle Benutzer)",
    };
}

/// <summary>Liest und schaltet Autostart-Einträge – identisch zum Mechanismus des Task-Managers (StartupApproved).</summary>
public static class StartupManager
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static List<StartupEntry> Load()
    {
        var list = new List<StartupEntry>();
        ReadRun(list, RegistryHive.CurrentUser, RunPath, StartupKind.UserRun);
        ReadRun(list, RegistryHive.LocalMachine, RunPath, StartupKind.MachineRun);
        ReadRun(list, RegistryHive.LocalMachine, Run32Path, StartupKind.MachineRun32);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupKind.UserFolder);
        ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupKind.CommonFolder);
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupEntry> list, RegistryHive hive, string path, StartupKind kind)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key == null) return;
            foreach (var valueName in key.GetValueNames())
            {
                var command = key.GetValue(valueName)?.ToString();
                if (string.IsNullOrWhiteSpace(command)) continue;
                var exe = ExtractExecutable(command);
                list.Add(Create(valueName, command, exe, kind, valueName, null));
            }
        }
        catch { }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupKind kind)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.GetFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string target = file, args = "";
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    (target, args) = ResolveShortcut(file);
                var command = string.IsNullOrEmpty(args) ? target : $"{target} {args}";
                list.Add(Create(Path.GetFileNameWithoutExtension(file), command, target, kind, fileName, file));
            }
        }
        catch { }
    }

    private static StartupEntry Create(string fallbackName, string command, string? exe, StartupKind kind, string approvedName, string? shortcut)
    {
        string name = fallbackName, publisher = "";
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) name = info.FileDescription.Trim();
                publisher = info.CompanyName?.Trim() ?? "";
            }
            catch { }
        }
        return new StartupEntry
        {
            Name = name,
            Command = command,
            FilePath = exe,
            Publisher = publisher,
            Kind = kind,
            ApprovedValueName = approvedName,
            ShortcutPath = shortcut,
            Icon = IconCache.Get(exe),
            IsEnabled = IsApproved(kind, approvedName),
        };
    }

    private static (RegistryHive Hive, string Path) ApprovedKey(StartupKind kind) => kind switch
    {
        StartupKind.UserRun => (RegistryHive.CurrentUser, ApprovedBase + "Run"),
        StartupKind.MachineRun => (RegistryHive.LocalMachine, ApprovedBase + "Run"),
        StartupKind.MachineRun32 => (RegistryHive.LocalMachine, ApprovedBase + "Run32"),
        StartupKind.UserFolder => (RegistryHive.CurrentUser, ApprovedBase + "StartupFolder"),
        _ => (RegistryHive.LocalMachine, ApprovedBase + "StartupFolder"),
    };

    private static bool IsApproved(StartupKind kind, string valueName)
    {
        try
        {
            var (hive, path) = ApprovedKey(kind);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key?.GetValue(valueName) is byte[] { Length: > 0 } data)
                return (data[0] & 1) == 0; // gerade = aktiv, ungerade = deaktiviert
        }
        catch { }
        return true;
    }

    /// <summary>Aktiviert/deaktiviert einen Eintrag. Wirft UnauthorizedAccessException, wenn Adminrechte fehlen.</summary>
    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        var (hive, path) = ApprovedKey(entry.Kind);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(path, true) ?? throw new UnauthorizedAccessException();
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        key.SetValue(entry.ApprovedValueName, data, RegistryValueKind.Binary);
    }

    public static string? ExtractExecutable(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables(command).Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return cmd[..(exe + 4)];
        int space = cmd.IndexOf(' ');
        return space > 0 ? cmd[..space] : cmd;
    }

    private static (string Target, string Args) ResolveShortcut(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return (path, "");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath;
            string args = shortcut.Arguments;
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            return (string.IsNullOrEmpty(target) ? path : target, args ?? "");
        }
        catch
        {
            return (path, "");
        }
    }
}
