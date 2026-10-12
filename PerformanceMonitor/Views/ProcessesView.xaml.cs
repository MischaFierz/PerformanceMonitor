using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public sealed class ProcessRow(string key) : ObservableObject
{
    public string Key { get; } = key;
    public List<int> Pids { get; set; } = new();

    private string _name = "";
    private string? _description, _path;
    private int _pid, _count;
    private bool _isGroup;
    private double _cpu, _io, _gpu;
    private long _workingSet;
    private ImageSource? _icon;

    public string Name { get => _name; set => SetField(ref _name, value); }
    public string? Description { get => _description; set => SetField(ref _description, value); }
    public string? Path { get => _path; set => SetField(ref _path, value); }
    public ImageSource? Icon { get => _icon; set => SetField(ref _icon, value); }
    public bool IsGroup { get => _isGroup; set => SetField(ref _isGroup, value); }

    public int Pid { get => _pid; set { if (SetField(ref _pid, value)) OnPropertyChanged(nameof(PidText)); } }
    public int Count { get => _count; set { if (SetField(ref _count, value)) OnPropertyChanged(nameof(PidText)); } }
    public string PidText => IsGroup ? $"{Count} Proz." : Pid.ToString();

    public double Cpu { get => _cpu; set { if (SetField(ref _cpu, Math.Round(value, 1))) OnPropertyChanged(nameof(CpuText)); } }
    public long WorkingSet { get => _workingSet; set { if (SetField(ref _workingSet, value)) OnPropertyChanged(nameof(RamText)); } }
    public double IoBps { get => _io; set { if (SetField(ref _io, Math.Round(value / 10240) * 10240)) OnPropertyChanged(nameof(IoText)); } }
    public double Gpu { get => _gpu; set { if (SetField(ref _gpu, Math.Round(value))) OnPropertyChanged(nameof(GpuText)); } }

    public string CpuText => $"{Cpu:0.0} %";
    public string RamText => Fmt.Bytes(WorkingSet);
    public string IoText => Fmt.MBps(IoBps);
    public string GpuText => $"{Gpu:0} %";
}

public partial class ProcessesView : UserControl, IPage
{
    private readonly ObservableCollection<ProcessRow> _rows = new();
    private readonly Dictionary<string, ProcessRow> _map = new();
    private readonly ListCollectionView _view;
    private Snapshot? _last;
    private string _search = "";
    private bool _grouped = true;

    public ProcessesView()
    {
        InitializeComponent();

        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _view.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        _view.IsLiveSorting = true;
        foreach (var p in new[] { "Cpu", "WorkingSet", "IoBps", "Gpu", "Name", "Pid", "Description" })
            _view.LiveSortingProperties.Add(p);
        _view.Filter = Matches;
        Grid.ItemsSource = _view;

        App.Monitor.SnapshotReady += s => Dispatcher.BeginInvoke(() =>
        {
            _last = s;
            if (IsVisible) Apply(s);
        });
    }

    public void OnNavigatedTo()
    {
        if (_last != null) Apply(_last);
    }

    private bool Matches(object o)
    {
        if (_search.Length == 0) return true;
        var r = (ProcessRow)o;
        return r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
               || (r.Description?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
               || r.Pid.ToString() == _search;
    }

    private void Apply(Snapshot s)
    {
        var groups = new Dictionary<string, List<ProcessInfo>>();
        foreach (var p in s.Processes)
        {
            var key = _grouped ? p.Name.ToLowerInvariant() : p.Pid.ToString();
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<ProcessInfo>(1);
            list.Add(p);
        }

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!groups.ContainsKey(_rows[i].Key))
            {
                _map.Remove(_rows[i].Key);
                _rows.RemoveAt(i);
            }
        }

        foreach (var (key, list) in groups)
        {
            bool isNew = !_map.TryGetValue(key, out var row);
            row ??= new ProcessRow(key);
            var first = list[0];
            var withPath = list.FirstOrDefault(p => p.Path != null) ?? first;
            row.Name = first.Name;
            row.IsGroup = _grouped && list.Count > 1;
            row.Count = list.Count;
            row.Pid = first.Pid;
            row.Pids = list.Select(p => p.Pid).ToList();
            row.Description = withPath.Description;
            row.Path = withPath.Path;
            row.Icon = withPath.Icon;
            row.Cpu = list.Sum(p => p.Cpu);
            row.WorkingSet = list.Sum(p => p.WorkingSet);
            row.IoBps = list.Sum(p => p.IoBps);
            row.Gpu = Math.Min(100, list.Sum(p => p.Gpu));
            if (isNew)
            {
                _map[key] = row;
                _rows.Add(row);
            }
        }

        CountText.Text = _grouped
            ? $"{groups.Count} Programme · {s.Processes.Count} Prozesse"
            : $"{s.Processes.Count} Prozesse";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        _view.Refresh();
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        _grouped = GroupToggle.IsChecked == true;
        _rows.Clear();
        _map.Clear();
        if (_last != null) Apply(_last);
    }

    private ProcessRow? Selected => Grid.SelectedItem as ProcessRow;

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        if (KnownProcesses.IsCritical(row.Name))
        {
            MessageBox.Show($"„{row.Name}“ ist ein wichtiger Systemprozess. Das Beenden könnte Windows zum Absturz bringen und ist deshalb gesperrt.",
                "Prozess beenden", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var what = row.Count > 1 ? $"„{row.Description ?? row.Name}“ ({row.Count} Prozesse)" : $"„{row.Description ?? row.Name}“ (PID {row.Pid})";
        if (MessageBox.Show($"{what} wirklich beenden?\n\nNicht gespeicherte Daten gehen dabei verloren.", "Prozess beenden",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var (_, failed) = ProcessActions.Kill(row.Pids);
        if (failed > 0)
            MessageBox.Show(App.IsAdmin
                    ? $"{failed} Prozess(e) konnten nicht beendet werden (geschützter Prozess)."
                    : $"{failed} Prozess(e) konnten nicht beendet werden. Starte PerfPulse als Administrator (Einstellungen), um auch Systemprozesse zu verwalten.",
                "Prozess beenden", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (Selected?.Path is { } path) ProcessActions.OpenFileLocation(path);
        else if (Selected != null)
            MessageBox.Show("Der Speicherort dieses Prozesses ist nicht verfügbar (Systemprozess oder fehlende Rechte).", "PerfPulse");
    }

    private void SearchWeb_Click(object sender, RoutedEventArgs e)
    {
        if (Selected != null) ProcessActions.SearchWeb(Selected.Name);
    }

    private void Priority_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row || sender is not MenuItem { Tag: string tag }) return;
        var priority = Enum.Parse<ProcessPriorityClass>(tag);
        int failed = ProcessActions.SetPriority(row.Pids, priority);
        if (failed > 0)
            MessageBox.Show($"Die Priorität konnte für {failed} Prozess(e) nicht geändert werden.", "PerfPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenLocation_Click(sender, e);
}
