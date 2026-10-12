using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class SystemView : UserControl, IPage
{
    private bool _loaded;

    public SystemView()
    {
        InitializeComponent();
    }

    public async void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        var s = await SystemInfoService.GetSummaryAsync();
        var drives = await Task.Run(() => SystemInfoService.GetDrives());
        LoadingText.Visibility = Visibility.Collapsed;

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        AddSection("Betriebssystem", "", "#60A5FA",
        [
            ("Version", s.OsName),
            ("Build", s.OsVersion),
            ("Computername", Environment.MachineName),
            ("Benutzer", Environment.UserName),
            ("Laufzeit", Fmt.Uptime(uptime)),
            ("Gestartet am", (DateTime.Now - uptime).ToString("dd.MM.yyyy, HH:mm")),
        ]);
        AddSection("Prozessor", "", "#22D3EE",
        [
            ("Modell", s.CpuName),
            ("Kerne / Threads", $"{s.Cores} / {s.Threads}"),
            ("Basistakt", s.MaxClockMhz > 0 ? $"{s.MaxClockMhz / 1000.0:0.00} GHz" : "–"),
            ("Architektur", Environment.Is64BitOperatingSystem ? "64-Bit (x64)" : "32-Bit"),
        ]);
        var gpuItems = new List<(string, string)>();
        foreach (var g in s.Gpus)
        {
            gpuItems.Add(("Modell", g.Name));
            if (g.Driver.Length > 0) gpuItems.Add(("Treiber", g.Driver));
            if (g.Resolution.Length > 0) gpuItems.Add(("Anzeige", g.Resolution));
        }
        if (gpuItems.Count == 0) gpuItems.Add(("Modell", "Keine erkannt"));
        AddSection("Grafik", "", "#F472B6", gpuItems);
        AddSection("Arbeitsspeicher", "", "#A78BFA",
        [
            ("Installiert", Fmt.Bytes(s.RamTotal)),
            ("Bestückung", s.RamDetails.Length > 0 ? s.RamDetails : "–"),
            ("Aktuell belegt", App.Monitor.Latest is { } snap ? $"{Fmt.Bytes(snap.RamUsed)} ({snap.RamPercent:0} %)" : "–"),
        ]);
        AddSection("Gerät", "", "#34D399",
        [
            ("Hersteller", s.Manufacturer.Length > 0 ? s.Manufacturer : "–"),
            ("Modell", s.Model.Length > 0 ? s.Model : "–"),
            ("Mainboard", s.Board.Length > 0 ? s.Board : "–"),
            ("BIOS", s.Bios.Length > 0 ? s.Bios : "–"),
        ]);
        AddSection("PerfPulse", "", "#FBBF24",
        [
            ("Version", App.Version),
            ("Modus", App.IsAdmin ? "Administrator" : "Standard"),
            ("GPU-Messung", App.Monitor.GpuAvailable ? "verfügbar" : "nicht verfügbar"),
            (".NET", Environment.Version.ToString()),
        ]);

        DriveList.ItemsSource = drives;
    }

    private void AddSection(string title, string glyph, string color, IList<(string Key, string Value)> items)
    {
        var accent = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        var soft = new SolidColorBrush(Color.FromArgb(0x26, accent.Color.R, accent.Color.G, accent.Color.B));

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(10), Background = soft,
            Child = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 16, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center },
        });
        header.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H2"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < items.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = items[i].Key, Style = (Style)FindResource("Caption"), FontSize = 13, Margin = new Thickness(0, 4, 0, 4) };
            var value = new TextBlock { Text = items[i].Value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(key, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(key);
            grid.Children.Add(value);
        }

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(grid);
        var column = LeftColumn.Children.Count <= RightColumn.Children.Count ? LeftColumn : RightColumn;
        column.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = panel, Margin = new Thickness(0, 0, 12, 12) });
    }
}
