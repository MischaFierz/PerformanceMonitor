using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class CleanerView : UserControl, IPage
{
    private readonly List<CleanupCategory> _categories = CleanupService.CreateCategories();
    private bool _busy, _analyzed;

    public CleanerView()
    {
        InitializeComponent();
        var view = new ListCollectionView(_categories);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(CleanupCategory.Group)));
        CategoryList.ItemsSource = view;
        AgeBox.SelectedIndex = 0;
    }

    public void OnNavigatedTo()
    {
        if (!_analyzed) _ = AnalyzeAsync();
        else foreach (var c in _categories) c.CheckRunning();
    }

    private void UpdateTotal()
    {
        var selected = _categories.Where(c => c.IsSelected && !c.AdminMissing).ToList();
        long total = selected.Where(c => c.Size > 0).Sum(c => c.Size);
        TotalText.Text = _analyzed ? Fmt.Bytes(total) : "–";
        TotalLabel.Text = $"Ausgewählt ({selected.Count} von {_categories.Count} Bereichen)";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy;
    }

    private async Task AnalyzeAsync()
    {
        if (_busy) return;
        SetBusy(true);
        StatusText.Text = "Analysiere …";
        foreach (var c in _categories)
        {
            c.Status = "wird gemessen …";
            var (size, files) = await Task.Run(() => CleanupService.Measure(c));
            c.FileCount = files;
            c.Size = size;
            c.CheckRunning();
            c.Status = c.AdminMissing ? "Adminrechte nötig" : "";
            _analyzed = true;
            UpdateTotal();
        }
        long all = _categories.Where(c => !c.AdminMissing && c.Size > 0).Sum(c => c.Size);
        StatusText.Text = $"Insgesamt {Fmt.Bytes(all)} in {_categories.Count} Bereichen gefunden. Wähle aus, was entfernt werden soll."
                          + (App.IsAdmin ? "" : " Für Windows-Bereiche mit „Admin“ PerfPulse als Administrator starten (Einstellungen).");
        SetBusy(false);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private void Selection_Click(object sender, RoutedEventArgs e) => UpdateTotal();

    private void Option_Changed(object sender, SelectionChangedEventArgs e) { }

    private void QuickSelect_Click(object sender, RoutedEventArgs e)
    {
        var mode = (string)((Button)sender).Tag;
        foreach (var c in _categories)
        {
            if (c.AdminMissing) { c.IsSelected = false; continue; }
            c.IsSelected = mode switch
            {
                "all" => true,
                "recommended" => c.Recommended,
                "caches" => !c.IsRecycleBin && !c.Name.Contains("Update") && !c.Name.Contains("Speicherabbild") && !c.Name.Contains("Übermittlung"),
                "browser" => c.Group == "Browser",
                _ => false,
            };
        }
        UpdateTotal();
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var selected = _categories.Where(c => c.IsSelected && !c.AdminMissing).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Bitte wähle mindestens einen Bereich aus.", "Bereinigung", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int? minAge = (AgeBox.SelectedItem as ComboBoxItem)?.Tag is string t ? int.Parse(t) : null;
        bool closeApps = CloseAppsToggle.IsChecked == true;
        var running = selected.Where(c => c.CheckRunning()).ToList();

        var list = string.Join("\n", selected.Take(12).Select(c => $"  • {c.Name}")) + (selected.Count > 12 ? $"\n  … und {selected.Count - 12} weitere" : "");
        var notes = "";
        if (selected.Any(c => c.IsRecycleBin)) notes += "\n\n⚠ Der Papierkorb wird endgültig geleert!";
        if (minAge == 0) notes += "\n\n⚠ Es werden auch ganz neue Dateien gelöscht.";
        if (running.Count > 0)
            notes += closeApps
                ? $"\n\nFolgende Programme werden geschlossen: {string.Join(", ", running.Select(c => c.Name))}"
                : $"\n\nHinweis: {string.Join(", ", running.Select(c => c.Name))} läuft gerade – gesperrte Dateien werden übersprungen.";
        if (MessageBox.Show($"Folgende Bereiche werden bereinigt:\n\n{list}{notes}\n\nFortfahren?", "Bereinigung",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetBusy(true);
        CleanProgress.Visibility = Visibility.Visible;
        CleanProgress.Value = 0;
        bool removeEmpty = EmptyFoldersToggle.IsChecked == true;

        if (closeApps && running.Count > 0)
        {
            StatusText.Text = "Schliesse Programme …";
            foreach (var c in running) await Task.Run(() => CleanupService.CloseApps(c));
            await Task.Delay(800);
        }

        long freed = 0;
        int skipped = 0, deleted = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            var c = selected[i];
            StatusText.Text = $"Bereinige {c.Name} …";
            c.Status = "wird bereinigt …";
            var result = await Task.Run(() => CleanupService.Clean(c, minAge, removeEmpty));
            freed += result.Freed;
            skipped += result.Skipped;
            deleted += result.Deleted;
            var (size, files) = await Task.Run(() => CleanupService.Measure(c));
            c.FileCount = files;
            c.Size = size;
            c.CheckRunning();
            c.Status = result.Freed > 0 ? $"{Fmt.Bytes(result.Freed)} entfernt ✓" : "nichts zu entfernen";
            CleanProgress.Value = (i + 1.0) / selected.Count;
        }
        UpdateTotal();
        StatusText.Text = $"Fertig! {Fmt.Bytes(freed)} freigegeben ({deleted:N0} Dateien)."
                          + (skipped > 0 ? $" {skipped:N0} Dateien wurden übersprungen (in Verwendung oder neuer als das Mindestalter)." : "");
        CleanProgress.Visibility = Visibility.Collapsed;
        SetBusy(false);
    }
}
