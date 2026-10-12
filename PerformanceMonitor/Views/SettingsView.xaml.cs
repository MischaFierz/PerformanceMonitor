using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public partial class SettingsView : UserControl
{
    private readonly bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        var s = App.Settings;
        AutostartToggle.IsChecked = SelfAutostart.IsEnabled;
        TrayToggle.IsChecked = s.MinimizeToTray;
        MinimizedToggle.IsChecked = s.StartMinimized;
        AlertsToggle.IsChecked = s.AlertsEnabled;
        CpuSlider.Value = s.CpuAlertThreshold;
        RamSlider.Value = s.RamAlertThreshold;
        SelectByTag(IntervalBox, s.UpdateIntervalMs);
        SelectByTag(DurationBox, s.AlertDurationSec);
        VersionText.Text = $"Version {App.Version}" + (App.IsAdmin ? " · Administrator-Modus" : "");
        AdminButton.IsEnabled = !App.IsAdmin;
        UpdateSliderTexts();
        _loading = false;
    }

    private static void SelectByTag(ComboBox box, int value)
    {
        var items = box.Items.Cast<ComboBoxItem>().ToList();
        box.SelectedItem = items.FirstOrDefault(i => int.Parse((string)i.Tag) == value) ?? items[1];
    }

    private static int TagOf(ComboBox box) => int.Parse((string)((ComboBoxItem)box.SelectedItem).Tag);

    private void UpdateSliderTexts()
    {
        CpuThresholdText.Text = $"{CpuSlider.Value:0} %";
        RamThresholdText.Text = $"{RamSlider.Value:0} %";
    }

    private void Save()
    {
        if (_loading) return;
        var s = App.Settings;
        s.MinimizeToTray = TrayToggle.IsChecked == true;
        s.StartMinimized = MinimizedToggle.IsChecked == true;
        s.AlertsEnabled = AlertsToggle.IsChecked == true;
        s.CpuAlertThreshold = (int)CpuSlider.Value;
        s.RamAlertThreshold = (int)RamSlider.Value;
        s.UpdateIntervalMs = TagOf(IntervalBox);
        s.AlertDurationSec = TagOf(DurationBox);
        App.Monitor.IntervalMs = s.UpdateIntervalMs;
        s.Save();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => Save();

    private void Interval_Changed(object sender, SelectionChangedEventArgs e) => Save();

    private void Duration_Changed(object sender, SelectionChangedEventArgs e) => Save();

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CpuThresholdText == null || RamThresholdText == null) return;
        UpdateSliderTexts();
        Save();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SelfAutostart.Set(AutostartToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            AutostartToggle.IsChecked = SelfAutostart.IsEnabled;
            MessageBox.Show($"Autostart konnte nicht geändert werden:\n{ex.Message}", "PerfPulse", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Admin_Click(object sender, RoutedEventArgs e) => App.RestartAsAdmin();

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDirectory);
        ProcessActions.OpenFileLocation(AppSettings.DataDirectory);
    }
}
