using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using PerformanceMonitor.Converters;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class ScanView : UserControl
{
    public static event Action<ScanResult>? ScanCompleted;

    private CancellationTokenSource? _cts;
    private ScanResult? _result;

    public ScanView()
    {
        InitializeComponent();
        DurationBox.SelectedIndex = App.Settings.ScanDurationSec switch { <= 10 => 0, >= 40 => 2, _ => 1 };
        Ring.Value = 0;
        LoadHistory(ScanHistory.Load());
    }

    private void LoadHistory(List<ScanHistoryEntry> history)
    {
        HistoryList.ItemsSource = history;
        HistoryPanel.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => StartScan();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    public async void StartScan()
    {
        if (_cts != null) return;
        int seconds = int.Parse((string)((ComboBoxItem)DurationBox.SelectedItem).Tag);
        if (App.Settings.ScanDurationSec != seconds)
        {
            App.Settings.ScanDurationSec = seconds;
            App.Settings.Save();
        }

        _cts = new CancellationTokenSource();
        ScanButton.IsEnabled = false;
        DurationBox.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        HeroTitle.Text = "Scan läuft …";
        HeroText.Text = "Nutze deinen PC ganz normal weiter – am besten genau so, wie wenn die Lags auftreten (z. B. Spiel oder Programm geöffnet lassen).";
        Ring.BeginAnimation(Controls.RingGauge.ValueProperty, null);
        Ring.RingBrush = (Brush)FindResource("AccentGradient");
        RingLabel.Text = "Fortschritt";

        var progress = new Progress<ScanProgress>(p =>
        {
            Progress.Value = p.Fraction;
            ProgressText.Text = p.Message;
            if (p.Live.Length > 0) ProgressLive.Text = p.Live;
            Ring.Value = p.Fraction * 100;
            RingValue.Text = $"{p.Fraction * 100:0}%";
        });

        try
        {
            var result = await new LagScanner(App.Monitor).RunAsync(seconds, progress, _cts.Token);
            ShowResult(result);
            LoadHistory(ScanHistory.Add(result));
            ScanCompleted?.Invoke(result);
            if (!Application.Current.MainWindow!.IsVisible)
                App.Current.ShowTrayMessage("Lag-Scan abgeschlossen", $"Score {result.Score} – {result.Headline}");
        }
        catch (OperationCanceledException)
        {
            HeroTitle.Text = "Scan abgebrochen";
            HeroText.Text = "Starte den Scan erneut, wann immer du möchtest.";
            RingValue.Text = "–";
            RingLabel.Text = "Abgebrochen";
            Ring.Value = 0;
        }
        catch (Exception ex)
        {
            HeroTitle.Text = "Scan fehlgeschlagen";
            HeroText.Text = ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ScanButton.IsEnabled = true;
            DurationBox.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ScanButtonText.Text = "Erneut scannen";
        }
    }

    private void ShowResult(ScanResult r)
    {
        _result = r;
        var brush = Palette.ForScore(r.Score);
        Ring.RingBrush = brush;
        Ring.Value = 0;
        Ring.BeginAnimation(Controls.RingGauge.ValueProperty,
            new DoubleAnimation(0, r.Score, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        RingValue.Text = r.Score.ToString();
        RingLabel.Text = r.Rating;
        HeroTitle.Text = r.Headline;
        HeroText.Text = r.Summary;

        FindingsList.ItemsSource = r.Findings;
        CulpritsList.ItemsSource = r.Culprits;

        StatsGrid.Children.Clear();
        AddStat("Ø Prozessor", $"{r.AvgCpu:0} %", $"Spitze {r.PeakCpu:0} %", "#22D3EE");
        AddStat("Ø Arbeitsspeicher", $"{r.AvgRam:0} %", "belegt", "#A78BFA");
        AddStat("Ø Datenträger", Fmt.Percent(r.AvgDisk), "aktive Zeit", "#FBBF24");
        AddStat("Ø Grafikkarte", Fmt.Percent(r.AvgGpu), "Auslastung", "#F472B6");
        AddStat("Treiber-Latenz", r.AvgDpc is double d ? $"{d:0.0} %" : "–", "DPC-Zeit", "#34D399");

        ResultsPanel.Visibility = Visibility.Visible;
        ExportButton.Visibility = Visibility.Visible;
    }

    private void AddStat(string title, string value, string sub, string color)
    {
        var accent = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Caption") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = accent, Margin = new Thickness(0, 4, 0, 0) });
        panel.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("Caption"), FontSize = 11 });
        var border = new Border { Style = (Style)FindResource("Card"), Child = panel, Padding = new Thickness(16, 14, 16, 14) };
        int index = StatsGrid.Children.Count;
        border.Margin = new Thickness(index == 0 ? 0 : 6, 0, index == 4 ? 0 : 6, 0);
        StatsGrid.Children.Add(border);
    }

    private async void FindingAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Finding f } || f.Action == null) return;
        if (f.ConfirmText != null &&
            MessageBox.Show(f.ConfirmText, "PerfPulse", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            f.ActionStatus = "Wird ausgeführt …";
            var status = await f.Action();
            f.ActionStatus = status ?? "";
            if (status != null) f.ActionDone = true;
        }
        catch (Exception ex)
        {
            f.ActionStatus = "Fehler: " + ex.Message;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Scan-Bericht speichern",
            Filter = "HTML-Bericht (*.html)|*.html",
            FileName = $"PerfPulse-Bericht-{_result.Time:yyyy-MM-dd_HHmm}.html",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog() != true) return;
        var sys = await SystemInfoService.GetSummaryAsync();
        await File.WriteAllTextAsync(dlg.FileName, ReportExporter.ToHtml(_result, sys));
        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }
}
