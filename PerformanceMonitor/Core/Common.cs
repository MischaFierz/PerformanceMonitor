using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PerformanceMonitor.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public static class Fmt
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 0) return "–";
        int i = 0;
        while (bytes >= 1024 && i < Units.Length - 1) { bytes /= 1024; i++; }
        return i == 0 ? $"{bytes:0} {Units[i]}" : $"{bytes:0.0} {Units[i]}";
    }

    public static string Rate(double bytesPerSecond) => Bytes(bytesPerSecond) + "/s";

    public static string MBps(double bytesPerSecond) => $"{bytesPerSecond / 1048576d:0.0} MB/s";

    public static string Percent(double? value) =>
        value is null || double.IsNaN(value.Value) ? "–" : $"{value.Value:0} %";

    public static string Ago(DateTime time)
    {
        var d = DateTime.Now - time;
        if (d.TotalDays < 1) return "heute";
        if (d.TotalDays < 2) return "gestern";
        if (d.TotalDays < 31) return $"vor {(int)d.TotalDays} Tagen";
        if (d.TotalDays < 365) { int m = (int)(d.TotalDays / 30.4); return m <= 1 ? "vor 1 Monat" : $"vor {m} Monaten"; }
        int y = (int)(d.TotalDays / 365.25);
        return y <= 1 ? "vor 1 Jahr" : $"vor {y} Jahren";
    }

    public static string Uptime(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays} T {t.Hours} Std {t.Minutes} Min" : $"{t.Hours} Std {t.Minutes} Min";
}

/// <summary>Cache für Programm-Icons (thread-sicher, liefert eingefrorene ImageSources).</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _default;

    public static ImageSource? Default
    {
        get
        {
            if (_default != null) return _default;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Application.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                _default = src;
            }
            catch { }
            return _default;
        }
    }

    /// <summary>Icon für beliebige Dateien: Programme einzeln, alle anderen pro Dateiendung gecacht.</summary>
    public static ImageSource? GetForFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".lnk" or ".ico" or "") return Get(path);
        lock (Cache)
            if (Cache.TryGetValue("ext:" + ext, out var cached)) return cached;
        var img = Get(path);
        lock (Cache) Cache["ext:" + ext] = img;
        return img;
    }

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Default;
        lock (Cache)
            if (Cache.TryGetValue(path, out var cached)) return cached;

        ImageSource? img = null;
        try
        {
            if (File.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    img = src;
                }
            }
        }
        catch { }

        img ??= Default;
        lock (Cache) Cache[path] = img;
        return img;
    }
}
