using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PerformanceMonitor.Converters;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public sealed class TopProcessRow : ObservableObject
{
    private string _title = "", _sub = "", _cpuText = "", _ramText = "";
    private double _cpu;
    private ImageSource? _icon;

    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Sub { get => _sub; set => SetField(ref _sub, value); }
    public double Cpu { get => _cpu; set => SetField(ref _cpu, value); }
    public string CpuText { get => _cpuText; set => SetField(ref _cpuText, value); }
    public string RamText { get => _ramText; set => SetField(ref _ramText, value); }
    public ImageSource? Icon { get => _icon; set => SetField(ref _icon, value); }
}

public partial class DashboardView : UserControl, IPage
{
    private readonly ObservableCollection<TopProcessRow> _top = new();
    private bool _updatingPlans;

    public DashboardView()
    {
        InitializeComponent();
        for (int i = 0; i < 6; i++) _top.Add(new TopProcessRow());
        TopList.ItemsSource = _top;
        NetCard.ChartMaximum = double.NaN;

        App.Monitor.SnapshotReady += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        ScanView.ScanCompleted += r => Dispatcher.BeginInvoke(() => ShowLastScan(r.Score, r.Rating, r.Time));

        var history = ScanHistory.Load();
        if (history.Count > 0) ShowLastScan(history[0].Score, history[0].Rating, history[0].Time);

        LoadStaticInfo();
    }

    public void OnNavigatedTo() => RefreshPlans();

    private async void LoadStaticInfo()
    {
        var s = await SystemInfoService.GetSummaryAsync();
        CpuCard.Subtitle = s.CpuName;
        RamCard.Subtitle = s.RamDetails.Length > 0 ? s.RamDetails : Fmt.Bytes(s.RamTotal);
        GpuCard.Subtitle = s.GpuShort;
        OsText.Text = $"{s.OsName} {s.OsVersion}";
        if (!App.Monitor.GpuAvailable) GpuCard.Detail = "GPU-Leistungsindikatoren nicht verfügbar";
        try
        {
            var system = await Task.Run(() => SystemInfoService.GetDrives());
            var sys = system.FirstOrDefault(d => d.IsSystem);
            if (sys != null) DiskCard.Subtitle = $"{sys.Letter} {sys.Type} · {Fmt.Bytes(sys.Free)} frei von {Fmt.Bytes(sys.Total)}";
        }
        catch { }
    }

    private void OnSnapshot(Snapshot s)
    {
        CpuCard.Push(s.Cpu);
        RamCard.Push(s.RamPercent);
        GpuCard.Push(s.Gpu ?? 0);
        DiskCard.Push(s.DiskActive ?? 0);
        NetCard.Push(s.NetDownBps, s.NetUpBps);

        if (!IsVisible) return;

        CpuCard.Value = $"{s.Cpu:0} %";
        CpuCard.Detail = string.Join("  ·  ", new[]
        {
            s.CpuFrequencyMhz is double f ? $"{f / 1000:0.00} GHz" : null,
            TemperatureService.Build(s).FirstOrDefault(t => t.Kind == HeatKind.Cpu && !t.Estimated) is { } temp ? temp.ValueText : null,
            $"{Environment.ProcessorCount} Threads",
            s.ProcessorQueue is double q ? $"Warteschlange {q:0}" : null,
        }.Where(x => x != null));

        RamCard.Value = $"{s.RamPercent:0} %";
        RamCard.Detail = $"{Fmt.Bytes(s.RamUsed)} von {Fmt.Bytes(s.RamTotal)}  ·  Zugesichert {Fmt.Bytes(s.CommitUsed)} / {Fmt.Bytes(s.CommitLimit)}";

        if (s.Gpu is double gpu)
        {
            GpuCard.Value = $"{gpu:0} %";
            var topGpu = s.Processes.MaxBy(p => p.Gpu);
            GpuCard.Detail = topGpu is { Gpu: >= 1 } ? $"Hauptnutzer: {topGpu.Description ?? topGpu.Name} ({topGpu.Gpu:0} %)" : "Keine nennenswerte Last";
        }
        else GpuCard.Value = "n. v.";

        DiskCard.Value = s.DiskActive is double d ? $"{d:0} %" : "n. v.";
        DiskCard.Detail = $"Lesen {Fmt.Rate(s.DiskReadBps)}  ·  Schreiben {Fmt.Rate(s.DiskWriteBps)}";

        NetCard.Value = $"↓ {Fmt.Rate(s.NetDownBps)}";
        NetCard.Detail = $"↑ Upload {Fmt.Rate(s.NetUpBps)}";

        UptimeText.Text = Fmt.Uptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
        ProcText.Text = $"{s.Processes.Count} / {s.HandleCount:N0}";
        double dpc = (s.Dpc ?? 0) + (s.Interrupt ?? 0);
        DpcText.Text = s.Dpc == null ? "–" : $"{dpc:0.0} %";
        DpcText.Foreground = dpc >= 4 ? Palette.Warn : (Brush)FindResource("TextBrush");

        var top = s.Processes.Where(p => p.Pid != Environment.ProcessId)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                First = g.First(),
                Cpu = g.Sum(p => p.Cpu),
                Ram = g.Sum(p => p.WorkingSet),
                Count = g.Count(),
            })
            .OrderByDescending(x => x.Cpu).ThenByDescending(x => x.Ram)
            .Take(_top.Count).ToList();

        for (int i = 0; i < _top.Count; i++)
        {
            var row = _top[i];
            if (i >= top.Count) { row.Title = ""; continue; }
            var t = top[i];
            row.Title = t.First.Description ?? t.First.Name;
            row.Sub = t.Count > 1 ? $"{t.First.Name} · {t.Count} Prozesse" : t.First.Name;
            row.Icon = t.First.Icon;
            row.Cpu = t.Cpu;
            row.CpuText = $"{t.Cpu:0.0} %";
            row.RamText = Fmt.Bytes(t.Ram);
        }
    }

    private void ShowLastScan(int score, string rating, DateTime time)
    {
        LastScanScore.Text = score.ToString();
        LastScanLabel.Text = rating;
        LastScanTime.Text = $"Letzter Scan: {time:dd.MM.yyyy, HH:mm}";
        LastScanRing.RingBrush = Palette.ForScore(score);
        LastScanRing.BeginAnimation(Controls.RingGauge.ValueProperty,
            new DoubleAnimation(score, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void RefreshPlans()
    {
        try
        {
            _updatingPlans = true;
            var plans = PowerPlans.GetAll();
            PlanBox.ItemsSource = plans;
            PlanBox.SelectedItem = plans.FirstOrDefault(p => p.IsActive);
            var best = plans.FirstOrDefault(p => p.Id == PowerPlans.UltimatePerformance) ?? plans.FirstOrDefault(p => p.Id == PowerPlans.HighPerformance);
            BoostToggle.IsChecked = best != null && best.IsActive && App.Settings.PlanBeforeBoost != null;
        }
        catch { }
        finally
        {
            _updatingPlans = false;
        }
    }

    private void PlanBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingPlans || PlanBox.SelectedItem is not PowerPlan plan || plan.IsActive) return;
        if (!PowerPlans.SetActive(plan.Id))
            MessageBox.Show("Der Energieplan konnte nicht geändert werden.", "PerfPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (App.Settings.PlanBeforeBoost != null)
        {
            App.Settings.PlanBeforeBoost = null;
            App.Settings.Save();
        }
        RefreshPlans();
    }

    private void Boost_Click(object sender, RoutedEventArgs e)
    {
        bool enable = BoostToggle.IsChecked == true;
        if (enable)
        {
            var best = PowerPlans.GetBestPerformancePlan();
            if (best == null)
            {
                MessageBox.Show("Auf diesem Gerät ist kein Höchstleistungs-Energieplan verfügbar (typisch für Geräte mit „Modern Standby“).\n\nTipp: Stelle in den Windows-Einstellungen unter System › Energie den Energiemodus auf „Beste Leistung“.",
                    "Boost-Modus", MessageBoxButton.OK, MessageBoxImage.Information);
                BoostToggle.IsChecked = false;
                return;
            }
            App.Settings.PlanBeforeBoost = PowerPlans.GetActive();
            App.Settings.Save();
            PowerPlans.SetActive(best.Id);
            App.Current.ShowTrayMessage("Boost-Modus aktiv", $"Energieplan „{best.Name}“ ist aktiv. Schalte den Boost aus, um zurückzuwechseln.");
        }
        else
        {
            PowerPlans.SetActive(App.Settings.PlanBeforeBoost ?? PowerPlans.Balanced);
            App.Settings.PlanBeforeBoost = null;
            App.Settings.Save();
        }
        RefreshPlans();
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => App.StartScan();

    private void AllProcesses_Click(object sender, RoutedEventArgs e) => App.Navigate("processes");
}
