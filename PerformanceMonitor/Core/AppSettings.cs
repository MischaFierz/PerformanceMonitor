using System.Text.Json;
using Microsoft.Win32;

namespace PerformanceMonitor.Core;

public sealed class AppSettings
{
    public int UpdateIntervalMs { get; set; } = 1000;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Überschreibt die beim Build gesetzte Website-Adresse (leer = Standard).</summary>
    public string? UpdateServer { get; set; }
    public List<string> SeenNotifications { get; set; } = [];
    public List<string> DismissedNotifications { get; set; } = [];
    public bool AlertsEnabled { get; set; } = true;
    public int CpuAlertThreshold { get; set; } = 90;
    public int RamAlertThreshold { get; set; } = 90;
    public int AlertDurationSec { get; set; } = 30;
    public int ScanDurationSec { get; set; } = 20;
    public bool TrayHintShown { get; set; }
    public Guid? PlanBeforeBoost { get; set; }

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PerfPulse");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }
}

/// <summary>Autostart von PerfPulse selbst (HKCU\...\Run).</summary>
public static class SelfAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PerfPulse";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(ValueName, false);
    }
}
