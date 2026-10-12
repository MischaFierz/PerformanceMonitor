using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using PerformanceMonitor.Core;
using WinForms = System.Windows.Forms;

namespace PerformanceMonitor;

public partial class App : Application
{
    private const string MutexName = @"Local\PerfPulse.SingleInstance";
    private const string ShowEventName = @"Local\PerfPulse.Show";

    public static SystemMonitor Monitor { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static new App Current => (App)Application.Current;

    public bool IsExiting { get; private set; }

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;

    private DateTime? _cpuHighSince, _ramHighSince;
    private DateTime _lastCpuAlert = DateTime.MinValue, _lastRamAlert = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstance(e.Args.Contains("--restart")))
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        }
        catch { }

        // Bei Abmelden/Herunterfahren nicht in den Infobereich minimieren
        SessionEnding += (_, _) => IsExiting = true;

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"Ein unerwarteter Fehler ist aufgetreten:\n\n{args.Exception.Message}", "PerfPulse", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        Settings = AppSettings.Load();
        Monitor = new SystemMonitor { IntervalMs = Settings.UpdateIntervalMs };
        Monitor.SnapshotReady += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        Monitor.Start();

        CreateTray();

        _window = new MainWindow();
        MainWindow = _window;
        bool minimized = e.Args.Contains("--minimized") || Settings.StartMinimized;

        // Diagnose-Optionen: --page <name> öffnet eine Seite, --offscreen zeigt das Fenster unsichtbar ausserhalb des Bildschirms
        int pageArg = Array.IndexOf(e.Args, "--page");
        if (pageArg >= 0 && pageArg + 1 < e.Args.Length) _window.Navigate(e.Args[pageArg + 1]);
        if (e.Args.Contains("--offscreen"))
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Left = -10000;
            _window.Top = 0;
            _window.ShowActivated = false;
            _window.ShowInTaskbar = false;
            minimized = false;
        }
        int snapArg = Array.IndexOf(e.Args, "--snapshot");
        if (snapArg >= 0 && snapArg + 1 < e.Args.Length) _window.StartSnapshots(e.Args[snapArg + 1]);
        if (!minimized) _window.Show();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew && waitForPrevious)
            {
                try { createdNew = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { createdNew = true; }
            }
            _ownsMutex = createdNew;
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "PerfPulse",
            Icon = LoadTrayIcon(),
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("PerfPulse öffnen", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Lag-Scan starten", null, (_, _) => { ShowMainWindow(); _window?.StartScan(); });
        menu.Items.Add("Prozesse anzeigen", null, (_, _) => { ShowMainWindow(); Navigate("processes"); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) ShowMainWindow(); };
        _tray.BalloonTipClicked += (_, _) => { ShowMainWindow(); Navigate("processes"); };
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var info = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null)
                using (var s = info.Stream)
                    return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowTrayMessage(string title, string text, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info) =>
        _tray?.ShowBalloonTip(5000, title, text, icon);

    public void ShowMainWindow()
    {
        if (_window == null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    public static void Navigate(string page) => Current._window?.Navigate(page);

    public static void StartScan()
    {
        Current.ShowMainWindow();
        Current._window?.StartScan();
    }

    public static void ExitApp()
    {
        Current.IsExiting = true;
        Current._window?.Close();
        Current.Shutdown();
    }

    public static void RestartAsAdmin()
    {
        try
        {
            Current.ReleaseSingleInstance();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = true, Verb = "runas" });
            ExitApp();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Benutzer hat die UAC-Abfrage abgebrochen – Instanz wieder sichern
            Current._ownsMutex = Current._mutex?.WaitOne(0) ?? false;
        }
    }

    private void ReleaseSingleInstance()
    {
        if (!_ownsMutex) return;
        try { _mutex?.ReleaseMutex(); } catch { }
        _ownsMutex = false;
    }

    private void OnSnapshot(Snapshot s)
    {
        if (_tray != null)
        {
            var text = $"PerfPulse\nCPU {s.Cpu:0} %  ·  RAM {s.RamPercent:0} %";
            if (s.Gpu is double g) text += $"  ·  GPU {g:0} %";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }

        if (!Settings.AlertsEnabled) { _cpuHighSince = _ramHighSince = null; return; }
        var now = DateTime.Now;
        var duration = TimeSpan.FromSeconds(Settings.AlertDurationSec);
        var cooldown = TimeSpan.FromMinutes(10);

        if (s.Cpu >= Settings.CpuAlertThreshold)
        {
            _cpuHighSince ??= now;
            if (now - _cpuHighSince >= duration && now - _lastCpuAlert >= cooldown)
            {
                _lastCpuAlert = now;
                var top = s.Processes.Where(p => p.Pid != Environment.ProcessId).MaxBy(p => p.Cpu);
                ShowTrayMessage("Hohe CPU-Last", $"Die CPU ist seit {Settings.AlertDurationSec} s über {Settings.CpuAlertThreshold} % ausgelastet."
                    + (top != null ? $"\nHauptverursacher: {top.Description ?? top.Name} ({top.Cpu:0} %)" : ""), WinForms.ToolTipIcon.Warning);
            }
        }
        else _cpuHighSince = null;

        if (s.RamPercent >= Settings.RamAlertThreshold)
        {
            _ramHighSince ??= now;
            if (now - _ramHighSince >= duration && now - _lastRamAlert >= cooldown)
            {
                _lastRamAlert = now;
                var top = s.Processes.MaxBy(p => p.WorkingSet);
                ShowTrayMessage("Arbeitsspeicher fast voll", $"{s.RamPercent:0} % des Arbeitsspeichers sind belegt."
                    + (top != null ? $"\nGrösster Verbraucher: {top.Description ?? top.Name} ({Fmt.Bytes(top.WorkingSet)})" : ""), WinForms.ToolTipIcon.Warning);
            }
        }
        else _ramHighSince = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Monitor?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _showWait?.Unregister(null);
        ReleaseSingleInstance();
        base.OnExit(e);
    }
}
