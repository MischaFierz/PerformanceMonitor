using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class StartupView : UserControl, IPage
{
    private List<StartupEntry> _entries = new();

    public StartupView()
    {
        InitializeComponent();
        AdminBanner.Visibility = App.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    public void OnNavigatedTo() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        LoadingText.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _entries = await Task.Run(StartupManager.Load);
        Grid.ItemsSource = _entries;
        LoadingText.Visibility = Visibility.Collapsed;
        if (_entries.Count == 0)
        {
            LoadingText.Text = "Keine Autostart-Einträge gefunden.";
            LoadingText.Visibility = Visibility.Visible;
        }
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        ActiveCount.Text = _entries.Count(e => e.IsEnabled).ToString();
        TotalCount.Text = $"von {_entries.Count} Autostart-Einträgen · Weniger Autostart-Programme = schnellerer Systemstart und mehr freie Ressourcen";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupEntry entry }) return;
        try
        {
            StartupManager.SetEnabled(entry, entry.IsEnabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            entry.IsEnabled = !entry.IsEnabled;
            MessageBox.Show("Für diesen Eintrag sind Administratorrechte erforderlich.\n\nStarte PerfPulse über den Button oben als Administrator neu.",
                "Autostart", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            entry.IsEnabled = !entry.IsEnabled;
            MessageBox.Show($"Der Eintrag konnte nicht geändert werden:\n{ex.Message}", "Autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateCounts();
    }

    private void Location_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not StartupEntry entry) return;
        ProcessActions.OpenFileLocation(entry.ShortcutPath ?? entry.FilePath);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void Admin_Click(object sender, RoutedEventArgs e) => App.RestartAsAdmin();
}
