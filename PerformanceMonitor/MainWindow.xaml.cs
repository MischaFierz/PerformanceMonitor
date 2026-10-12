using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using PerformanceMonitor.Core;
using PerformanceMonitor.Views;

namespace PerformanceMonitor;

public interface IPage
{
    void OnNavigatedTo();
}

public partial class MainWindow : Window
{
    private readonly Dictionary<string, (Func<FrameworkElement> Factory, string Title, string Subtitle)> _pageDefs;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Dictionary<string, RadioButton> _nav;
    private readonly ScanView _scanView = new();

    public MainWindow()
    {
        InitializeComponent();

        _pageDefs = new()
        {
            ["dashboard"] = (() => new DashboardView(), "Dashboard", "Live-Übersicht über die Leistung deines Systems"),
            ["processes"] = (() => new ProcessesView(), "Prozesse", "Alle laufenden Programme mit CPU-, RAM-, Datenträger- und GPU-Verbrauch"),
            ["scan"] = (() => _scanView, "Lag-Scan", "Finde heraus, was dein Gerät zum Laggen bringt"),
            ["temps"] = (() => new TemperatureView(), "Temperatur", "Sensoren, Hitze-Hotspots und Kühlungsanalyse"),
            ["background"] = (() => new BackgroundView(), "Hintergrund", "Unsichtbar laufende Programme analysieren und aufräumen"),
            ["startup"] = (() => new StartupView(), "Autostart", "Programme, die automatisch mit Windows starten"),
            ["cleaner"] = (() => new CleanerView(), "Bereinigung", "Speicherplatz freigeben durch das Entfernen unnötiger Dateien"),
            ["storage"] = (() => new StorageView(), "Speicheranalyse", "Grosse, selten genutzte und doppelte Dateien finden"),
            ["system"] = (() => new SystemView(), "Systeminfo", "Hardware und Betriebssystem im Überblick"),
            ["settings"] = (() => new SettingsView(), "Einstellungen", "PerfPulse an deine Wünsche anpassen"),
        };
        _nav = new()
        {
            ["dashboard"] = NavDashboard,
            ["processes"] = NavProcesses,
            ["scan"] = NavScan,
            ["temps"] = NavTemps,
            ["background"] = NavBackground,
            ["startup"] = NavStartup,
            ["cleaner"] = NavCleaner,
            ["storage"] = NavStorage,
            ["system"] = NavSystem,
            ["settings"] = NavSettings,
        };

        AdminText.Text = App.IsAdmin ? "Administrator-Modus" : "Standard-Modus";
        AdminDot.Fill = App.IsAdmin ? (System.Windows.Media.Brush)FindResource("GoodBrush") : (System.Windows.Media.Brush)FindResource("SubTextBrush");

        App.Monitor.SnapshotReady += s => Dispatcher.BeginInvoke(() => UpdateMini(s));
        SourceInitialized += (_, _) => ApplyWindowEffects();
        StateChanged += (_, _) => UpdateWindowState();
        Closing += OnClosing;

        Navigate("dashboard");
    }

    public void Navigate(string key)
    {
        if (_nav.TryGetValue(key, out var rb))
        {
            if (rb.IsChecked == true) ShowPage(key);
            else rb.IsChecked = true;
        }
    }

    /// <summary>Diagnose: speichert alle 2 s ein Bild des Fensterinhalts (funktioniert auch ausserhalb des Bildschirms).</summary>
    public void StartSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)(RootBorder.ActualWidth * dpi.DpiScaleX), (int)(RootBorder.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
                var bg = new System.Windows.Shapes.Rectangle { Width = RootBorder.ActualWidth, Height = RootBorder.ActualHeight, Fill = Background };
                bg.Measure(new Size(bg.Width, bg.Height));
                bg.Arrange(new Rect(0, 0, bg.Width, bg.Height));
                bmp.Render(bg);
                bmp.Render(RootBorder);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var fs = File.Create(Path.Combine(folder, "snapshot.png"));
                enc.Save(fs);
            }
            catch { }
        };
        timer.Start();
    }

    public void StartScan()
    {
        Navigate("scan");
        _scanView.StartScan();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pageDefs == null) return; // während InitializeComponent
        if (sender is RadioButton { CommandParameter: string key }) ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (!_pageDefs.TryGetValue(key, out var def)) return;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = def.Factory();
            _pages[key] = page;
        }
        PageTitle.Text = def.Title;
        PageSubtitle.Text = def.Subtitle;
        if (!ReferenceEquals(PageHost.Content, page))
        {
            PageHost.Content = page;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        (page as IPage)?.OnNavigatedTo();
    }

    private void UpdateMini(Snapshot s)
    {
        MiniCpu.Text = $"{s.Cpu:0} %";
        MiniCpuBar.Value = s.Cpu;
        MiniRam.Text = $"{s.RamPercent:0} %";
        MiniRamBar.Value = s.RamPercent;
    }

    private void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1, round = 2, border = 0x003D2B25; // COLORREF (BGR) für #252B3D
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
        Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));  // DWMWA_WINDOW_CORNER_PREFERENCE
        Native.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
    }

    private void UpdateWindowState()
    {
        bool max = WindowState == WindowState.Maximized;
        RootBorder.Margin = max ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Wiederherstellen" : "Maximieren";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.Current.IsExiting) return;
        if (App.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!App.Settings.TrayHintShown)
            {
                App.Settings.TrayHintShown = true;
                App.Settings.Save();
                App.Current.ShowTrayMessage("PerfPulse läuft weiter", "PerfPulse überwacht dein System im Hintergrund. Klicke auf das Symbol im Infobereich, um es wieder zu öffnen.");
            }
            return;
        }
        e.Cancel = true;
        Dispatcher.BeginInvoke(App.ExitApp);
    }
}
