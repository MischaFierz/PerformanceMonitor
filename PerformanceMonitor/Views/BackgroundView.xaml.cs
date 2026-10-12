using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class BackgroundView : UserControl, IPage
{
    private List<BackgroundApp> _apps = new();
    private bool _busy;
    private DateTime _lastAnalysis = DateTime.MinValue;

    public BackgroundView()
    {
        InitializeComponent();
    }

    public void OnNavigatedTo()
    {
        if (DateTime.Now - _lastAnalysis > TimeSpan.FromMinutes(2)) _ = AnalyzeAsync();
    }

    /// <summary>Sammelt einige Messungen, damit die CPU-Werte nicht nur eine Momentaufnahme sind.</summary>
    private static async Task<List<Snapshot>> CollectAsync(int count)
    {
        var samples = new List<Snapshot>();
        var tcs = new TaskCompletionSource();
        void Handler(Snapshot s)
        {
            lock (samples)
            {
                samples.Add(s);
                if (samples.Count >= count) tcs.TrySetResult();
            }
        }
        App.Monitor.SnapshotReady += Handler;
        try { await Task.WhenAny(tcs.Task, Task.Delay(count * 2500 + 2000)); }
        finally { App.Monitor.SnapshotReady -= Handler; }
        lock (samples)
        {
            if (samples.Count == 0 && App.Monitor.Latest != null) samples.Add(App.Monitor.Latest);
            return samples.ToList();
        }
    }

    private async Task AnalyzeAsync()
    {
        if (_busy) return;
        _busy = true;
        AnalyzeButton.IsEnabled = CleanButton.IsEnabled = false;
        HeadText.Text = "Hintergrundprogramme werden analysiert …";
        try
        {
            var samples = await CollectAsync(3);
            if (samples.Count == 0) return;
            _apps = await Task.Run(() => BackgroundAnalyzer.Analyze(samples));
            _lastAnalysis = DateTime.Now;
            AppList.ItemsSource = _apps;
            UpdateStats();
        }
        finally
        {
            _busy = false;
            AnalyzeButton.IsEnabled = true;
            CleanButton.IsEnabled = _apps.Any(a => a.Verdict == BgVerdict.Unnecessary && !a.IsClosed);
        }
    }

    private void UpdateStats()
    {
        var running = _apps.Where(a => !a.IsClosed).ToList();
        var unnecessary = running.Where(a => a.Verdict == BgVerdict.Unnecessary).ToList();
        StatCount.Text = running.Count.ToString();
        StatCountSub.Text = $"belegen zusammen {Fmt.Bytes(running.Sum(a => a.Ram))} RAM";
        StatUnnecessary.Text = unnecessary.Count.ToString();
        StatUnnecessarySub.Text = unnecessary.Count > 0 ? $"{Fmt.Bytes(unnecessary.Sum(a => a.Ram))} RAM könnten frei werden" : "alles im grünen Bereich";
        StatAutostart.Text = running.Count(a => a.InAutostart).ToString();

        HeadText.Text = unnecessary.Count switch
        {
            0 => "Keine unnötigen Hintergrundprogramme",
            1 => "1 unnötiges Hintergrundprogramm gefunden",
            _ => $"{unnecessary.Count} unnötige Hintergrundprogramme gefunden",
        };
        EmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.IsEnabled = unnecessary.Count > 0 && !_busy;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var sel = _apps.Where(a => a.IsSelected && !a.IsClosed).ToList();
        CloseSelectedText.Text = sel.Count == 0 ? "Ausgewählte beenden" : $"{sel.Count} beenden ({Fmt.Bytes(sel.Sum(a => a.Ram))})";
        CloseSelectedButton.IsEnabled = sel.Count > 0;
    }

    private void Select_Click(object sender, RoutedEventArgs e) => UpdateSelection();

    private void SelectUnnecessary_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _apps) a.IsSelected = !a.IsClosed && a.Verdict == BgVerdict.Unnecessary;
        UpdateSelection();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async void CloseUnnecessary_Click(object sender, RoutedEventArgs e) =>
        await CloseAsync(_apps.Where(a => a.Verdict == BgVerdict.Unnecessary && !a.IsClosed).ToList());

    private async void CloseSelected_Click(object sender, RoutedEventArgs e) =>
        await CloseAsync(_apps.Where(a => a.IsSelected && !a.IsClosed).ToList());

    private async void CloseOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BackgroundApp app }) await CloseAsync([app]);
    }

    private async Task CloseAsync(List<BackgroundApp> apps)
    {
        if (apps.Count == 0) return;
        var list = string.Join("\n", apps.Take(12).Select(a => $"  • {a.Name} ({a.RamText})")) + (apps.Count > 12 ? $"\n  … und {apps.Count - 12} weitere" : "");
        bool important = apps.Any(a => a.Verdict == BgVerdict.Important);
        if (MessageBox.Show($"Folgende Programme werden beendet:\n\n{list}"
                            + (important ? "\n\n⚠ Darunter ist Sicherheitssoftware – das wird nicht empfohlen." : "")
                            + "\n\nNicht gespeicherte Daten in diesen Programmen gehen verloren. Fortfahren?",
                "Hintergrundprogramme beenden", MessageBoxButton.YesNo, important ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        long freed = 0;
        foreach (var app in apps)
        {
            var (ok, failed) = await Task.Run(() => ProcessActions.Kill(app.Pids));
            if (ok > 0 && failed == 0)
            {
                app.IsClosed = true;
                app.IsSelected = false;
                app.Status = app.InAutostart ? "Beendet ✓ – startet beim nächsten Hochfahren wieder" : "Beendet ✓";
                freed += app.Ram;
            }
            else app.Status = ok > 0 ? $"{ok} beendet, {failed} fehlgeschlagen" : "Fehlgeschlagen (Adminrechte?)";
        }
        UpdateStats();
        if (freed > 0)
            App.Current.ShowTrayMessage("Hintergrund aufgeräumt", $"{Fmt.Bytes(freed)} Arbeitsspeicher freigegeben.");
    }

    private void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BackgroundApp { Startup: { } entry } app }) return;
        try
        {
            StartupManager.SetEnabled(entry, false);
            entry.IsEnabled = false;
            app.StartupDisabled = true;
            app.Status = "Autostart deaktiviert ✓";
            UpdateStats();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show("Für diesen Autostart-Eintrag sind Administratorrechte erforderlich.", "Autostart", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
