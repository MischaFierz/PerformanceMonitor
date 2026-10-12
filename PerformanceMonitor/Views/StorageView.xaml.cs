using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class StorageView : UserControl
{
    private sealed record Scope(string Label, string[] Paths)
    {
        public override string ToString() => Label;
    }

    private sealed record SizeOption(string Label, long Bytes)
    {
        public override string ToString() => Label;
    }

    private const long MB = 1024 * 1024;

    private static readonly SizeOption[] LargeSizes =
    [
        new("ab 50 MB", 50 * MB), new("ab 100 MB", 100 * MB), new("ab 250 MB", 250 * MB), new("ab 500 MB", 500 * MB), new("ab 1 GB", 1024 * MB),
    ];

    private static readonly SizeOption[] DupSizes =
    [
        new("ab 100 KB", 100 * 1024), new("ab 1 MB", MB), new("ab 10 MB", 10 * MB), new("ab 100 MB", 100 * MB),
    ];

    private readonly ObservableCollection<FileItem> _large = new();
    private readonly ObservableCollection<FileItem> _dups = new();
    private readonly ListCollectionView _largeView;
    private CancellationTokenSource? _cts;

    public sealed record DupGroup(string Title, List<FileItem> Items);

    /// <summary>Baut die Gruppenansicht der Duplikate aus der flachen Liste neu auf.</summary>
    private void RebuildDupGroups() =>
        DupList.ItemsSource = _dups.GroupBy(i => i.GroupKey)
            .Select(g => new DupGroup(g.First().GroupTitle, g.ToList()))
            .ToList();
    private bool _largeScanned, _dupScanned;

    private bool IsDupMode => ModeDup.IsChecked == true;
    private ObservableCollection<FileItem> Current => IsDupMode ? _dups : _large;

    public StorageView()
    {
        InitializeComponent();

        _largeView = (ListCollectionView)CollectionViewSource.GetDefaultView(_large);
        _largeView.SortDescriptions.Add(new SortDescription(nameof(FileItem.Size), ListSortDirection.Descending));
        _largeView.Filter = o => RareToggle.IsChecked != true || ((FileItem)o).IsRarelyUsed;
        LargeGrid.ItemsSource = _largeView;

        BuildScopes();
        ApplyMode();
    }

    private void BuildScopes()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var scopes = new List<Scope>
        {
            new("Benutzerordner (empfohlen)", [profile]),
            new("Downloads", [Path.Combine(profile, "Downloads")]),
            new("Dokumente", [Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)]),
            new("Desktop", [Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)]),
            new("Bilder, Videos & Musik", [
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)]),
        };
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable))
            scopes.Add(new Scope($"Ganzes Laufwerk {d.Name.TrimEnd('\\')} ({Fmt.Bytes(d.AvailableFreeSpace)} frei)", [d.Name]));
        scopes.Add(new Scope("Anderen Ordner wählen …", []));
        ScopeBox.ItemsSource = scopes;
        ScopeBox.SelectedIndex = 0;
    }

    private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeBox.SelectedItem is not Scope { Paths.Length: 0 }) return;
        var dlg = new OpenFolderDialog { Title = "Ordner für die Suche wählen" };
        if (dlg.ShowDialog() == true)
        {
            var scopes = (List<Scope>)ScopeBox.ItemsSource;
            var custom = new Scope(dlg.FolderName, [dlg.FolderName]);
            scopes.Insert(scopes.Count - 1, custom);
            ScopeBox.Items.Refresh();
            ScopeBox.SelectedItem = custom;
        }
        else ScopeBox.SelectedIndex = 0;
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (LargeGrid != null) ApplyMode();
    }

    private void ApplyMode()
    {
        bool dup = IsDupMode;
        LargeGrid.Visibility = dup ? Visibility.Collapsed : Visibility.Visible;
        DupList.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;
        RareToggle.Visibility = dup ? Visibility.Collapsed : Visibility.Visible;
        AutoSelectButton.Visibility = dup ? Visibility.Visible : Visibility.Collapsed;
        ModeDescription.Text = dup
            ? "Findet Dateien mit exakt gleichem Inhalt – auch wenn sie unterschiedlich heissen (z. B. doppelte Fotos, mehrfach heruntergeladene Dateien). Verglichen wird per Prüfsumme (SHA-256), nicht nur über den Namen."
            : "Findet die grössten Dateien und zeigt, wann sie zuletzt verändert oder heruntergeladen wurden. Lange unveränderte Dateien, alte Installer, Archive und Abbilder sind oft unnötig und belegen viel Platz. Programm- und Systemordner werden zum Schutz übersprungen.";
        MinSizeBox.ItemsSource = dup ? DupSizes : LargeSizes;
        MinSizeBox.SelectedIndex = 1;
        UpdateSummary();
        UpdateSelection();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null || ScopeBox.SelectedItem is not Scope { Paths.Length: > 0 } scope) return;
        bool dup = IsDupMode;
        long minSize = ((SizeOption)MinSizeBox.SelectedItem).Bytes;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        var progress = new Progress<FileScanProgress>(p =>
        {
            ProgressText.Text = p.Message;
            Progress.Value = p.Fraction < 0 ? 0 : p.Fraction;
            Progress.IsIndeterminate = p.Fraction < 0;
        });
        var sw = Stopwatch.StartNew();
        try
        {
            var token = _cts.Token;
            var items = await Task.Run(() => dup
                ? FileScanner.FindDuplicates(scope.Paths, minSize, progress, token)
                : FileScanner.FindLargeFiles(scope.Paths, minSize, progress, token), token);

            var target = dup ? _dups : _large;
            target.Clear();
            foreach (var i in items) target.Add(i);
            if (dup) { _dupScanned = true; RebuildDupGroups(); } else _largeScanned = true;
            SummaryText.ToolTip = $"Suche dauerte {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Die Suche ist fehlgeschlagen:\n{ex.Message}", "Speicheranalyse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            UpdateSummary();
            UpdateSelection();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ModeLarge.IsEnabled = ModeDup.IsEnabled = !busy;
        ScopeBox.IsEnabled = MinSizeBox.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = !busy && Current.Any(i => i.IsSelected);
    }

    private void UpdateSummary()
    {
        bool dup = IsDupMode;
        bool scanned = dup ? _dupScanned : _largeScanned;
        var list = Current;

        if (dup)
        {
            int groups = list.Select(i => i.GroupKey).Distinct().Count();
            long wasted = list.GroupBy(i => i.GroupKey).Sum(g => g.First().Size * (g.Count() - 1));
            int copies = list.Count - groups;
            SummaryText.Text = scanned ? $"{groups} Gruppen  ·  {copies} doppelte Dateien  ·  {Fmt.Bytes(wasted)} verschwendet" : "Duplikate";
        }
        else
        {
            long total = list.Sum(i => i.Size);
            var rare = list.Where(i => i.IsRarelyUsed).ToList();
            SummaryText.Text = scanned
                ? $"{list.Count} grosse Dateien  ·  {Fmt.Bytes(total)}  ·  davon {rare.Count} über 90 Tage unverändert ({Fmt.Bytes(rare.Sum(i => i.Size))})"
                : "Grosse Dateien";
        }

        bool empty = list.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyGlyph.Text = dup ? "" : "";
        EmptyText.Text = !scanned
            ? "Wähle oben einen Ort und klicke auf „Suche starten“."
            : dup ? "Keine doppelten Dateien gefunden. 🎉" : "Keine Dateien über der gewählten Grösse gefunden.";
    }

    private void UpdateSelection()
    {
        var sel = Current.Where(i => i.IsSelected).ToList();
        SelectionText.Text = sel.Count == 0 ? "Keine Dateien ausgewählt" : $"{sel.Count} ausgewählt  ·  {Fmt.Bytes(sel.Sum(i => i.Size))}";
        DeleteButton.IsEnabled = sel.Count > 0 && _cts == null;
    }

    private void Select_Click(object sender, RoutedEventArgs e) => UpdateSelection();

    private void RareToggle_Click(object sender, RoutedEventArgs e) => _largeView.Refresh();

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in Current) i.IsSelected = false;
        UpdateSelection();
    }

    private void AutoSelect_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _dups) i.IsSelected = !i.IsSuggestedKeep;
        UpdateSelection();
    }

    private FileItem? SelectedRow => (IsDupMode ? null : LargeGrid.SelectedItem) as FileItem;

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedRow ?? Current.FirstOrDefault(i => i.IsSelected);
        if (item != null) ProcessActions.OpenFileLocation(item.Path);
    }

    private void OpenItemLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileItem item }) ProcessActions.OpenFileLocation(item.Path);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is { } item) ProcessActions.OpenFileLocation(item.Path);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = Current.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        if (IsDupMode)
        {
            // Sicherheit: aus jeder Gruppe muss mindestens eine Datei erhalten bleiben
            var fullGroups = _dups.GroupBy(i => i.GroupKey).Count(g => g.All(i => i.IsSelected));
            if (fullGroups > 0)
            {
                MessageBox.Show($"In {fullGroups} Gruppe(n) sind alle Kopien ausgewählt. Mindestens eine Datei pro Gruppe muss erhalten bleiben.\n\nTipp: „Kopien automatisch auswählen“ behält immer das Original.",
                    "Duplikate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        long size = selected.Sum(i => i.Size);
        var preview = string.Join("\n", selected.Take(8).Select(i => $"  • {i.Name}")) + (selected.Count > 8 ? $"\n  … und {selected.Count - 8} weitere" : "");
        if (MessageBox.Show($"{selected.Count} Datei(en) ({Fmt.Bytes(size)}) in den Papierkorb verschieben?\n\n{preview}\n\nDu kannst sie dort jederzeit wiederherstellen.",
                "In Papierkorb verschieben", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var owner = new WindowInteropHelper(Window.GetWindow(this)!).Handle;
        Mouse.OverrideCursor = Cursors.Wait;
        List<string> removed;
        try { removed = FileScanner.MoveToRecycleBin(selected.Select(i => i.Path).ToList(), owner); }
        finally { Mouse.OverrideCursor = null; }

        var removedSet = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
        foreach (var i in Current.Where(i => removedSet.Contains(i.Path)).ToList()) Current.Remove(i);

        if (IsDupMode)
        {
            // Gruppen mit nur noch einer Datei sind keine Duplikate mehr
            foreach (var g in _dups.GroupBy(i => i.GroupKey).Where(g => g.Count() < 2).ToList())
                foreach (var i in g.ToList()) _dups.Remove(i);
            RebuildDupGroups();
        }

        long freed = selected.Where(i => removedSet.Contains(i.Path)).Sum(i => i.Size);
        int failed = selected.Count - removed.Count;
        UpdateSummary();
        UpdateSelection();
        MessageBox.Show($"{removed.Count} Datei(en) ({Fmt.Bytes(freed)}) wurden in den Papierkorb verschoben."
                        + (failed > 0 ? $"\n{failed} Datei(en) konnten nicht verschoben werden (in Verwendung oder keine Berechtigung)." : "")
                        + "\n\nDer Platz wird endgültig frei, sobald du den Papierkorb leerst (z. B. unter „Bereinigung“).",
            "Speicheranalyse", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
