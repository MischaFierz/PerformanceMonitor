using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PerformanceMonitor.Converters;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Views;

public sealed record SensorRow(string Label, string ValueText, Brush Brush, bool Estimated, string Source, double BarWidth);

public partial class TemperatureView : UserControl, IPage
{
    private CancellationTokenSource? _testCts;
    private ThermalTestResult? _testResult;
    private DateTime _lastAdvice = DateTime.MinValue;
    private bool _configured;

    public TemperatureView()
    {
        InitializeComponent();
        TempChart.Maximum = 100;
        App.Monitor.SnapshotReady += s => Dispatcher.BeginInvoke(() => OnSnapshot(s));
        Configure();
    }

    public void OnNavigatedTo()
    {
        if (App.Monitor.Latest is { } s) OnSnapshot(s, force: true);
    }

    private async void Configure()
    {
        var sys = await SystemInfoService.GetSummaryAsync();
        Visual.Configure(sys.IsLaptop, sys.HasDedicatedGpu);
        DeviceTitle.Text = string.IsNullOrWhiteSpace(sys.Model) ? (sys.IsLaptop ? "Laptop" : "Desktop-PC")
            : sys.Model.StartsWith(sys.Manufacturer, StringComparison.OrdinalIgnoreCase) ? sys.Model : $"{sys.Manufacturer} {sys.Model}".Trim();
        _configured = true;
        if (App.Monitor.Latest is { } s) OnSnapshot(s, force: true);
    }

    private void OnSnapshot(Snapshot s) => OnSnapshot(s, force: false);

    private void OnSnapshot(Snapshot s, bool force)
    {
        var sensors = TemperatureService.Build(s);
        var cpu = sensors.First(t => t.Kind == HeatKind.Cpu);
        TempChart.Push(cpu.Celsius);
        if (!IsVisible && !force) return;

        if (_configured) Visual.SetTemperatures(sensors);
        ChartValue.Text = cpu.ValueText;
        ChartValue.Foreground = new SolidColorBrush(TemperatureService.ColorFor(cpu.Celsius));

        var throttling = s.ThermalZones.Where(z => z.PassiveLimit is < 99.5).ToList();
        ThrottleText.Text = throttling.Count > 0 ? $"aktiv ({throttling.Min(z => z.PassiveLimit):0} % Leistung)" : (s.ThermalZones.Count > 0 ? "keine" : "nicht messbar");
        ThrottleText.Foreground = throttling.Count > 0 ? Palette.Crit : Palette.Good;

        SensorList.ItemsSource = sensors
            .Where(t => t.Kind != HeatKind.Other || !t.Estimated)
            .Select(t =>
            {
                var brush = new SolidColorBrush(TemperatureService.ColorFor(t.Celsius));
                brush.Freeze();
                return new SensorRow(t.Label, t.ValueText, brush, t.Estimated, t.Source, Math.Clamp((t.Celsius - 20) / 80, 0.03, 1) * 250);
            }).ToList();
        int measured = sensors.Count(t => !t.Estimated);
        SensorCount.Text = $"{measured} gemessen · {sensors.Count - measured} geschätzt";

        UpdateStatus(sensors, throttling.Count > 0);
        if (force || DateTime.Now - _lastAdvice > TimeSpan.FromSeconds(5))
        {
            _lastAdvice = DateTime.Now;
            AdviceList.ItemsSource = BuildAdvice(sensors, s, throttling.Count > 0);
        }
    }

    private void UpdateStatus(List<TempSensor> sensors, bool throttling)
    {
        var relevant = sensors.Where(t => t.Kind is not (HeatKind.Ambient or HeatKind.Other)).ToList();
        var worst = relevant.OrderByDescending(t => t.Level).ThenByDescending(t => t.Celsius).First();
        var hottest = relevant.Where(t => !t.Estimated).DefaultIfEmpty(worst).MaxBy(t => t.Celsius)!;

        (string title, SolidColorBrush brush, string glyph) = throttling
            ? ("Thermische Drosselung aktiv!", Palette.Crit, "")
            : worst.Level switch
            {
                HeatLevel.Critical => ($"{worst.Label} ist kritisch heiss", Palette.Crit, ""),
                HeatLevel.Hot => ($"{worst.Label} wird heiss", Palette.Warn, ""),
                HeatLevel.Warm => ($"Temperaturen in Ordnung – {worst.Label} ist warm", Palette.Info, ""),
                _ => ("Alle Temperaturen im grünen Bereich", Palette.Good, ""),
            };
        StatusTitle.Text = title;
        StatusIcon.Text = glyph;
        StatusIcon.Foreground = brush;
        StatusIconBorder.Background = Palette.Soft(brush);
        StatusText.Text = $"Heissester Punkt: {hottest.Label} mit {hottest.ValueText}. "
                          + (throttling ? "Windows reduziert die Leistung, um das Gerät zu schützen – das verursacht Lags." : "Fahre mit der Maus über die Bauteile im Modell für Details.");
    }

    private List<Finding> BuildAdvice(List<TempSensor> sensors, Snapshot s, bool throttling)
    {
        var list = new List<Finding>();
        if (_testResult is { } r)
            list.Add(new Finding { Severity = r.Severity, Title = $"Wärmetest: {r.Verdict}", Detail = r.Detail });

        if (throttling)
            list.Add(new Finding
            {
                Severity = Severity.Critical, Title = "Das System drosselt wegen Hitze",
                Detail = "Windows meldet aktive passive Kühlung – der Prozessor wird künstlich verlangsamt. Lüftungsschlitze freihalten, Lüfter von Staub befreien, Laptop auf eine harte Unterlage stellen.",
            });

        TempSensor? Get(HeatKind k) => sensors.Where(t => t.Kind == k).OrderBy(t => t.Estimated).ThenByDescending(t => t.Celsius).FirstOrDefault();

        if (Get(HeatKind.Cpu) is { } cpu)
        {
            var (sev, text) = cpu.Level switch
            {
                HeatLevel.Critical => (Severity.Critical, "Kritisch heiss. Dauerhaft über 95 °C drosselt der Prozessor stark und altert schneller."),
                HeatLevel.Hot => (Severity.Warning, "Heiss. Unter Volllast normal, im Leerlauf aber ein Zeichen für verstaubte Lüfter oder eingetrocknete Wärmeleitpaste."),
                HeatLevel.Warm => (Severity.Info, "Warm, aber im erlaubten Bereich."),
                _ => (Severity.Ok, "Im optimalen Bereich."),
            };
            list.Add(new Finding { Severity = sev, Title = $"Prozessor: {cpu.ValueText}", Detail = $"{text} Auslastung gerade {s.Cpu:0} %. Quelle: {cpu.Source}." });
        }

        if (Get(HeatKind.Gpu) is { Estimated: false } gpu && gpu.Level >= HeatLevel.Hot)
            list.Add(new Finding { Severity = gpu.Level == HeatLevel.Critical ? Severity.Critical : Severity.Warning, Title = $"Grafik: {gpu.ValueText}", Detail = "Die Grafik wird sehr warm. In Spielen ein FPS-Limit setzen oder Grafikdetails reduzieren." });

        if (Get(HeatKind.Battery) is { } bat)
            list.Add(bat.Level >= HeatLevel.Hot
                ? new Finding { Severity = Severity.Warning, Title = $"Akku: {bat.ValueText} – zu warm", Detail = "Hitze ist der grösste Feind von Akkus. Laptop nicht in der Sonne oder auf weichen Unterlagen betreiben; bei Dauerbetrieb am Netz die Ladegrenze (z. B. 80 %) im Hersteller-Tool aktivieren." }
                : new Finding { Severity = Severity.Ok, Title = $"Akku: {bat.ValueText}", Detail = "Optimale Temperatur für eine lange Akku-Lebensdauer." });

        if (Get(HeatKind.Skin) is { } skin && skin.Level >= HeatLevel.Warm)
            list.Add(new Finding { Severity = skin.Level >= HeatLevel.Hot ? Severity.Warning : Severity.Info, Title = $"Gehäuseoberfläche: {skin.ValueText}", Detail = "Die Oberfläche ist spürbar warm. Nicht auf dem Schoss oder auf Decken verwenden – eine Kühlunterlage senkt die Temperatur um mehrere Grad." });

        if (Get(HeatKind.Charger) is { } chg && chg.Level >= HeatLevel.Hot)
            list.Add(new Finding { Severity = Severity.Info, Title = $"Ladeelektronik: {chg.ValueText}", Detail = "Beim Schnellladen normal. Bleibt der Wert dauerhaft hoch, das Netzteil auf Originalzustand prüfen." });

        if (Get(HeatKind.Ssd) is { Estimated: false } ssd && ssd.Level >= HeatLevel.Hot)
            list.Add(new Finding { Severity = Severity.Warning, Title = $"SSD: {ssd.ValueText}", Detail = "SSDs drosseln ab ca. 70 °C ihre Geschwindigkeit. Ein Kühlkörper für die M.2-SSD hilft." });

        if (Get(HeatKind.Ambient) is { } amb && amb.Celsius >= 35)
            list.Add(new Finding { Severity = Severity.Info, Title = $"Lufteinlass: {amb.ValueText}", Detail = "Die angesaugte Luft ist bereits warm – das Gerät steht vermutlich zu nah an einer Wärmequelle oder die Lüftung ist blockiert." });

        var estimated = sensors.Where(t => t.Estimated).Select(t => t.Label).Distinct().ToList();
        if (estimated.Count > 0)
            list.Add(new Finding
            {
                Severity = Severity.Info, Title = "Nicht alle Bauteile haben einen Sensor",
                Detail = $"{string.Join(", ", estimated)}: Diese Werte werden aus der Auslastung geschätzt (≈)."
                         + (App.IsAdmin ? "" : " Mit Adminrechten kann zusätzlich die SSD-Temperatur gelesen werden."),
            });

        if (list.All(f => f.Severity <= Severity.Info) && !throttling)
            list.Insert(_testResult != null ? 1 : 0, new Finding { Severity = Severity.Ok, Title = "Kühlung arbeitet einwandfrei", Detail = "Keine Hitzeprobleme erkannt. Mit dem Wärmetest kannst du prüfen, wie sich das System unter Volllast verhält." });

        return list.OrderByDescending(f => f.Severity).ToList();
    }

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (Visual == null) return;
        double target = ViewExploded.IsChecked == true ? 1 : 0;
        Visual.BeginAnimation(Controls.HardwareVisual.ExplodeProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testCts != null) return;
        if (MessageBox.Show("Der Wärmetest belastet alle Prozessorkerne 30 Sekunden lang voll und beobachtet danach 10 Sekunden die Abkühlung.\n\nDer Lüfter wird dabei lauter und andere Programme laufen kurz langsamer. Moderne Prozessoren schützen sich selbst vor Überhitzung.\n\nTest starten?",
                "Wärmetest", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _testCts = new CancellationTokenSource();
        TestButton.IsEnabled = false;
        TestPanel.Visibility = Visibility.Visible;
        ViewExploded.IsChecked = true;
        var progress = new Progress<(double Fraction, string Text)>(p =>
        {
            TestProgress.Value = p.Fraction;
            TestProgressText.Text = p.Text;
        });
        try
        {
            _testResult = await ThermalTest.RunAsync(30, progress, _testCts.Token);
            if (App.Monitor.Latest is { } s) OnSnapshot(s, force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MessageBox.Show($"Der Wärmetest ist fehlgeschlagen:\n{ex.Message}", "Wärmetest", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _testCts.Dispose();
            _testCts = null;
            TestButton.IsEnabled = true;
            TestButtonText.Text = "Erneut testen";
            TestPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelTest_Click(object sender, RoutedEventArgs e) => _testCts?.Cancel();
}
