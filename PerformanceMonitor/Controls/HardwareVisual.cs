using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerformanceMonitor.Core;

namespace PerformanceMonitor.Controls;

/// <summary>
/// Isometrische Darstellung des Geräts (Laptop oder Desktop) – zusammengebaut oder als Explosionszeichnung –
/// mit Temperatur-Hotspots auf den einzelnen Bauteilen.
/// </summary>
public sealed class HardwareVisual : FrameworkElement
{
    private enum PartKind { Box, Board, Fan, Deck, Lid, Glass, Heatsink, Battery, Chip, Ram, Copper }

    private sealed class Part
    {
        public string Label = "";
        public string Description = "";
        public PartKind Kind;
        public double X, Y, Z, W, D, H;
        public Color Color;
        public double Lift;          // Höhe, um die das Teil in der Explosionsansicht angehoben wird
        public double ShiftY;        // Verschiebung nach hinten in der Explosionsansicht
        public HeatKind? Heat;
        public bool Covered;         // liegt unter Gehäuse/Glas -> Hotspot scheint im zusammengebauten Zustand durch
        public double? HeatX, HeatY; // eigener Hotspot-Mittelpunkt (z. B. Tastatur)
        public double GlowScale = 1;
        public bool Labeled = true;
    }

    public static readonly DependencyProperty ExplodeProperty = DependencyProperty.Register(
        nameof(Explode), typeof(double), typeof(HardwareVisual),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 = zusammengebaut, 1 = Explosionsansicht.</summary>
    public double Explode { get => (double)GetValue(ExplodeProperty); set => SetValue(ExplodeProperty, value); }

    private List<Part> _parts = new();
    private bool _isLaptop = true, _hasGpu;
    private readonly Dictionary<HeatKind, TempSensor> _temps = new();
    private readonly List<(Part Part, List<Point[]> Faces)> _hitFaces = new();
    private Part? _hover;
    private Point _mouse;
    private double _fanAngle;
    private readonly DispatcherTimer _timer;

    private const double LabelColumn = 210;
    private static readonly double Cos30 = Math.Cos(Math.PI / 6), Sin30 = Math.Sin(Math.PI / 6);

    public HardwareVisual()
    {
        BuildParts();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            double cpu = _temps.TryGetValue(HeatKind.Cpu, out var t) ? t.Celsius : 45;
            _fanAngle += 0.08 + Math.Clamp((cpu - 35) / 55, 0, 1) * 0.45;
            InvalidateVisual();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        ClipToBounds = true;
    }

    public void Configure(bool isLaptop, bool hasDedicatedGpu)
    {
        _isLaptop = isLaptop;
        _hasGpu = hasDedicatedGpu;
        BuildParts();
        InvalidateVisual();
    }

    public void SetTemperatures(IEnumerable<TempSensor> sensors)
    {
        _temps.Clear();
        foreach (var g in sensors.GroupBy(s => s.Kind))
        {
            // gemessene Werte vor Schätzungen, dann der heisseste Sensor
            var best = g.OrderBy(s => s.Estimated).ThenByDescending(s => s.Celsius).First();
            _temps[g.Key] = best;
        }
        // Integrierte Grafik sitzt auf dem Prozessor-Die
        if (!_hasGpu && _temps.TryGetValue(HeatKind.Gpu, out var gpu) && _temps.TryGetValue(HeatKind.Cpu, out var cpu) && gpu.Celsius > cpu.Celsius && !gpu.Estimated)
            _temps[HeatKind.Cpu] = cpu with { Celsius = gpu.Celsius, Source = cpu.Source + " / Grafik-Sensor" };
        InvalidateVisual();
    }

    // ===================== Modell =====================

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private void BuildParts()
    {
        _parts = _isLaptop ? BuildLaptop() : BuildDesktop();
    }

    private List<Part> BuildLaptop()
    {
        var p = new List<Part>
        {
            new() { Label = "Display", Description = "Bildschirmdeckel mit Panel", Kind = PartKind.Lid, X = 0, Y = -7, Z = 18, W = 320, D = 7, H = 205, Color = C("#2B3040"), Lift = 470, ShiftY = -70, Labeled = false },
            new() { Label = "Bodenplatte", Description = "Untere Gehäuseschale mit Lüftungsschlitzen", Kind = PartKind.Box, X = 0, Y = 0, Z = 0, W = 320, D = 220, H = 5, Color = C("#252A37"), Labeled = false },
            new() { Label = "Mainboard", Description = "Hauptplatine mit Spannungswandlern", Kind = PartKind.Board, X = 18, Y = 12, Z = 5, W = 214, D = 118, H = 2, Color = C("#15392B"), Lift = 120, Heat = HeatKind.Board, Covered = true, GlowScale = 0.8 },
            new() { Label = "Akku", Description = "Lithium-Ionen-Akku", Kind = PartKind.Battery, X = 18, Y = 140, Z = 5, W = 284, D = 68, H = 8, Color = C("#363B48"), Lift = 120, Heat = HeatKind.Battery, Covered = true, GlowScale = 0.7 },
            new() { Label = "Arbeitsspeicher", Description = "RAM-Module", Kind = PartKind.Ram, X = 28, Y = 22, Z = 7, W = 48, D = 11, H = 3, Color = C("#0F5F59"), Lift = 165, Heat = HeatKind.Ram, Covered = true },
            new() { Label = "Arbeitsspeicher", Kind = PartKind.Ram, X = 28, Y = 38, Z = 7, W = 48, D = 11, H = 3, Color = C("#0F5F59"), Lift = 165, Heat = HeatKind.Ram, Covered = true, Labeled = false },
            new() { Label = "Chipsatz (PCH)", Description = "Platform Controller Hub", Kind = PartKind.Chip, X = 60, Y = 94, Z = 7, W = 16, D = 16, H = 2, Color = C("#3B4252"), Lift = 165, Heat = HeatKind.Chipset, Covered = true },
            new() { Label = "Prozessor", Description = "CPU (mit integrierter Grafik)", Kind = PartKind.Chip, X = 92, Y = 44, Z = 7, W = 30, D = 30, H = 3, Color = C("#A1A1AA"), Lift = 165, Heat = HeatKind.Cpu, Covered = true, GlowScale = 1.3 },
            new() { Label = "SSD", Description = "M.2-SSD", Kind = PartKind.Chip, X = 150, Y = 98, Z = 7, W = 62, D = 16, H = 3, Color = C("#1F2937"), Lift = 165, Heat = HeatKind.Ssd, Covered = true },
            new() { Label = "Ladeelektronik", Description = "Ladeschaltung / Netzteilbuchse", Kind = PartKind.Chip, X = 204, Y = 18, Z = 7, W = 16, D = 12, H = 2, Color = C("#3B4252"), Lift = 165, Heat = HeatKind.Charger, Covered = true },
        };
        if (_hasGpu)
        {
            p.First(x => x.Label == "Prozessor").Description = "CPU";
            p.Add(new() { Label = "Grafikkarte", Description = "Dedizierte GPU", Kind = PartKind.Chip, X = 136, Y = 44, Z = 7, W = 32, D = 32, H = 3, Color = C("#52525B"), Lift = 165, Heat = HeatKind.Gpu, Covered = true, GlowScale = 1.3 });
            p.Add(new() { Label = "Kühlplatte", Description = "Kupfer-Kühlplatte (GPU)", Kind = PartKind.Copper, X = 133, Y = 41, Z = 10, W = 38, D = 38, H = 2, Color = C("#B87333"), Lift = 235, Labeled = false });
        }
        p.Add(new() { Label = "Kühlplatte", Description = "Kupfer-Kühlplatte (CPU)", Kind = PartKind.Copper, X = 89, Y = 41, Z = 10, W = 36, D = 36, H = 2, Color = C("#B87333"), Lift = 235, Labeled = false });
        p.Add(new() { Label = "Heatpipe", Description = "Leitet die Wärme zum Lüfter", Kind = PartKind.Copper, X = 118, Y = 54, Z = 10, W = 122, D = 8, H = 2, Color = C("#C2803F"), Lift = 235, Labeled = false });
        p.Add(new() { Label = "Lüfter", Description = "Radiallüfter – bläst die Wärme aus dem Gehäuse", Kind = PartKind.Fan, X = 238, Y = 28, Z = 5, W = 60, D = 60, H = 11, Color = C("#1E2230"), Lift = 235, Labeled = false });
        p.Add(new() { Label = "Gehäuseoberfläche", Description = "Tastatur-Oberteil mit Handballenauflage", Kind = PartKind.Deck, X = 0, Y = 0, Z = 14, W = 320, D = 220, H = 4, Color = C("#2E3446"), Lift = 360, Heat = HeatKind.Skin, HeatX = 160, HeatY = 80, GlowScale = 0.9 });
        return p;
    }

    private List<Part> BuildDesktop()
    {
        var p = new List<Part>
        {
            new() { Label = "Gehäuse", Description = "PC-Gehäuse (liegend dargestellt)", Kind = PartKind.Box, X = 0, Y = 0, Z = 0, W = 420, D = 210, H = 6, Color = C("#1F2430"), Labeled = false },
            new() { Label = "Gehäuse", Kind = PartKind.Box, X = 0, Y = 0, Z = 6, W = 420, D = 5, H = 80, Color = C("#232836"), Labeled = false },
            new() { Label = "Gehäuse", Kind = PartKind.Box, X = 0, Y = 5, Z = 6, W = 5, D = 205, H = 80, Color = C("#232836"), Labeled = false },
            new() { Label = "Mainboard", Description = "Hauptplatine", Kind = PartKind.Board, X = 20, Y = 20, Z = 6, W = 255, D = 172, H = 3, Color = C("#15392B"), Lift = 90, Heat = HeatKind.Board, Covered = true, GlowScale = 0.8 },
            new() { Label = "Netzteil", Description = "Netzteil (kein Sensor)", Kind = PartKind.Box, X = 292, Y = 20, Z = 6, W = 115, D = 85, H = 70, Color = C("#27272A"), Lift = 70, Labeled = false },
            new() { Label = "Laufwerkskäfig", Description = "Festplatten / Laufwerke", Kind = PartKind.Box, X = 300, Y = 125, Z = 6, W = 100, D = 70, H = 45, Color = C("#2A2F3B"), Lift = 70, Labeled = false },
            new() { Label = "Prozessor", Description = "CPU", Kind = PartKind.Chip, X = 110, Y = 55, Z = 9, W = 34, D = 34, H = 3, Color = C("#A1A1AA"), Lift = 130, Heat = HeatKind.Cpu, Covered = true, GlowScale = 1.3 },
            new() { Label = "SSD", Description = "M.2-SSD", Kind = PartKind.Chip, X = 120, Y = 112, Z = 9, W = 55, D = 12, H = 2, Color = C("#1F2937"), Lift = 130, Heat = HeatKind.Ssd, Covered = true },
            new() { Label = "Chipsatz", Description = "Chipsatz", Kind = PartKind.Chip, X = 205, Y = 125, Z = 9, W = 20, D = 20, H = 3, Color = C("#3B4252"), Lift = 130, Heat = HeatKind.Chipset, Covered = true },
        };
        for (int i = 0; i < 4; i++)
            p.Add(new() { Label = "Arbeitsspeicher", Description = "RAM-Module", Kind = PartKind.Ram, X = 170 + i * 11, Y = 38, Z = 9, W = 7, D = 72, H = 30, Color = C("#0F5F59"), Lift = 165, Heat = HeatKind.Ram, Covered = true, Labeled = i == 0 });
        p.Add(new() { Label = "CPU-Kühler", Description = "Tower-Kühler mit Lüfter", Kind = PartKind.Heatsink, X = 100, Y = 45, Z = 12, W = 54, D = 54, H = 56, Color = C("#9CA3AF"), Lift = 230, Labeled = false });
        p.Add(new() { Label = "Lüfter", Description = "CPU-Lüfter", Kind = PartKind.Fan, X = 103, Y = 48, Z = 68, W = 48, D = 48, H = 6, Color = C("#1E2230"), Lift = 230, Labeled = false });
        if (_hasGpu)
        {
            p.Add(new() { Label = "Grafikkarte", Description = "Dedizierte Grafikkarte", Kind = PartKind.Box, X = 35, Y = 140, Z = 9, W = 225, D = 42, H = 24, Color = C("#3F3F46"), Lift = 175, Heat = HeatKind.Gpu, Covered = true, GlowScale = 1.2 });
            p.Add(new() { Label = "Lüfter", Description = "Grafikkarten-Lüfter", Kind = PartKind.Fan, X = 68, Y = 144, Z = 33, W = 34, D = 34, H = 3, Color = C("#1E2230"), Lift = 175, Labeled = false });
            p.Add(new() { Label = "Lüfter", Description = "Grafikkarten-Lüfter", Kind = PartKind.Fan, X = 158, Y = 144, Z = 33, W = 34, D = 34, H = 3, Color = C("#1E2230"), Lift = 175, Labeled = false });
        }
        p.Add(new() { Label = "Seitenteil", Description = "Glas-Seitenteil", Kind = PartKind.Glass, X = 0, Y = 0, Z = 86, W = 420, D = 210, H = 3, Color = C("#93C5FD"), Lift = 330, Labeled = false });
        return p;
    }

    // ===================== Projektion =====================

    private static Point Iso(double x, double y, double z) => new((x - y) * Cos30, (x + y) * Sin30 - z);

    private double _scale = 1;
    private Vector _offset;

    private Point P(double x, double y, double z)
    {
        var p = Iso(x, y, z);
        return new Point(p.X * _scale + _offset.X, p.Y * _scale + _offset.Y);
    }

    private (double Z, double Y) Placed(Part p, double e) => (p.Z + e * p.Lift, p.Y + e * p.ShiftY);

    private void Fit(Size size)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        // Zoom folgt dem aktuellen Zustand: zusammengebaut gross, zerlegt passend herausgezoomt
        foreach (var e in new[] { Explode })
            foreach (var p in _parts)
            {
                var (z, y) = Placed(p, e);
                foreach (var cx in new[] { p.X, p.X + p.W })
                    foreach (var cy in new[] { y, y + p.D })
                        foreach (var cz in new[] { z, z + p.H })
                        {
                            var q = Iso(cx, cy, cz);
                            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                            minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                        }
            }
        double availW = Math.Max(50, size.Width - LabelColumn - 30);
        double availH = Math.Max(50, size.Height - 70);
        _scale = Math.Min(availW / (maxX - minX), availH / (maxY - minY));
        double drawW = (maxX - minX) * _scale, drawH = (maxY - minY) * _scale;
        _offset = new Vector(15 + (availW - drawW) / 2 - minX * _scale, 20 + (availH - drawH) / 2 - minY * _scale);
    }

    // ===================== Zeichnen =====================

    private static Color Shade(Color c, double f) =>
        Color.FromArgb(c.A, (byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));

    private static SolidColorBrush B(Color c, byte? alpha = null)
    {
        var b = new SolidColorBrush(alpha.HasValue ? Color.FromArgb(alpha.Value, c.R, c.G, c.B) : c);
        b.Freeze();
        return b;
    }

    private static StreamGeometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], true, true);
            ctx.PolyLineTo(pts[1..], true, true);
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width < 100 || size.Height < 100) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        Fit(size);
        double e = Explode;
        _hitFaces.Clear();

        // Bodenschatten
        var shadowCenter = P(_isLaptop ? 160 : 210, _isLaptop ? 110 : 105, 0);
        var shadow = new RadialGradientBrush(Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0));
        shadow.Freeze();
        dc.DrawEllipse(shadow, null, shadowCenter, 300 * _scale, 150 * _scale);

        var edge = new Pen(B(Color.FromArgb(38, 255, 255, 255)), 0.8);
        edge.Freeze();

        foreach (var part in _parts)
        {
            var faces = DrawPart(dc, part, e, edge);
            _hitFaces.Add((part, faces));
            DrawPartGlow(dc, part, e);
        }

        DrawSurfaceHotspots(dc, e);

        if (_hover != null)
        {
            var entry = _hitFaces.LastOrDefault(f => f.Part == _hover);
            if (entry.Faces != null)
            {
                var pen = new Pen(B(Color.FromRgb(0x22, 0xD3, 0xEE)), 2);
                foreach (var f in entry.Faces) dc.DrawGeometry(null, pen, Poly(f));
            }
        }

        DrawLabels(dc, e, size);
        DrawLegend(dc, size);
        if (_hover != null) DrawHoverInfo(dc, size);
    }

    private List<Point[]> DrawPart(DrawingContext dc, Part p, double e, Pen edge)
    {
        var (z, y) = Placed(p, e);
        double x = p.X, w = p.W, d = p.D, h = p.H;
        var faces = new List<Point[]>();

        if (p.Kind == PartKind.Fan)
        {
            DrawFan(dc, x + w / 2, y + d / 2, z, h, w / 2, p.Color, faces);
            return faces;
        }

        var top = new[] { P(x, y, z + h), P(x + w, y, z + h), P(x + w, y + d, z + h), P(x, y + d, z + h) };
        var right = new[] { P(x + w, y, z), P(x + w, y + d, z), P(x + w, y + d, z + h), P(x + w, y, z + h) };
        var front = new[] { P(x, y + d, z), P(x + w, y + d, z), P(x + w, y + d, z + h), P(x, y + d, z + h) };

        if (p.Kind == PartKind.Glass)
        {
            var glassTop = B(p.Color, (byte)(28 + 40 * e));
            dc.DrawGeometry(glassTop, new Pen(B(p.Color, 110), 1.2), Poly(top));
            dc.DrawGeometry(B(p.Color, 40), null, Poly(right));
            dc.DrawGeometry(B(p.Color, 50), null, Poly(front));
            // Spiegelung
            var a = P(x + w * 0.15, y, z + h); var b = P(x + w * 0.35, y, z + h);
            var c2 = P(x + w * 0.2, y + d, z + h); var d2 = P(x, y + d, z + h);
            dc.DrawGeometry(B(Colors.White, 14), null, Poly(a, b, c2, d2));
            faces.Add(top);
            return faces;
        }

        dc.DrawGeometry(B(Shade(p.Color, 0.72)), edge, Poly(right));
        dc.DrawGeometry(B(Shade(p.Color, 0.55)), edge, Poly(front));

        if (p.Kind == PartKind.Lid)
        {
            // Rückseite (oben) und Bildschirm (Vorderseite)
            dc.DrawGeometry(B(p.Color), edge, Poly(top));
            DrawScreen(dc, x, y + d, z, w, h);
        }
        else
        {
            Brush topBrush = p.Kind is PartKind.Copper or PartKind.Chip
                ? new LinearGradientBrush(Shade(p.Color, 1.18), Shade(p.Color, 0.9), 45)
                : B(p.Color);
            if (topBrush.CanFreeze) topBrush.Freeze();
            dc.DrawGeometry(topBrush, edge, Poly(top));
            DecorateTop(dc, p, x, y, z + h, w, d);
        }

        faces.Add(top);
        faces.Add(right);
        faces.Add(front);
        return faces;
    }

    private void DecorateTop(DrawingContext dc, Part p, double x, double y, double z, double w, double d)
    {
        switch (p.Kind)
        {
            case PartKind.Board:
            {
                var pen = new Pen(B(Color.FromArgb(55, 0x6E, 0xE7, 0xB7)), 0.7);
                for (int i = 1; i < 9; i++)
                {
                    double yy = y + d * i / 9.0;
                    dc.DrawLine(pen, P(x + 6, yy, z), P(x + w * (0.35 + 0.07 * (i % 4)), yy, z));
                    dc.DrawLine(pen, P(x + w * (0.35 + 0.07 * (i % 4)), yy, z), P(x + w * (0.45 + 0.07 * (i % 4)), yy + (i % 2 == 0 ? 4 : -4), z));
                }
                break;
            }
            case PartKind.Battery:
            {
                var pen = new Pen(B(Color.FromArgb(50, 255, 255, 255)), 0.8);
                for (int i = 1; i < 4; i++) dc.DrawLine(pen, P(x + w * i / 4, y + 4, z), P(x + w * i / 4, y + d - 4, z));
                break;
            }
            case PartKind.Ram:
            {
                var gold = new Pen(B(Color.FromRgb(0xD4, 0xA0, 0x17)), 1.2);
                if (w > d) dc.DrawLine(gold, P(x + 2, y + d - 1, z), P(x + w - 2, y + d - 1, z));
                var chip = B(Color.FromRgb(0x11, 0x18, 0x27));
                int n = 4;
                for (int i = 0; i < n; i++)
                {
                    if (w > d)
                    {
                        double cx = x + 3 + i * (w - 6) / n;
                        dc.DrawGeometry(chip, null, Poly(P(cx, y + 2, z), P(cx + (w - 6) / n - 2, y + 2, z), P(cx + (w - 6) / n - 2, y + d - 3, z), P(cx, y + d - 3, z)));
                    }
                }
                break;
            }
            case PartKind.Deck:
            {
                // Tastatur + Touchpad
                var key = B(Color.FromRgb(0x1A, 0x1E, 0x29));
                const int cols = 14, rows = 6;
                double kx0 = x + 30, ky0 = y + 18, kw = (w - 60) / cols, kd = 95.0 / rows;
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        double ax = kx0 + c * kw + 1.2, ay = ky0 + r * kd + 1.2;
                        dc.DrawGeometry(key, null, Poly(P(ax, ay, z), P(ax + kw - 2.4, ay, z), P(ax + kw - 2.4, ay + kd - 2.4, z), P(ax, ay + kd - 2.4, z)));
                    }
                var pad = B(Color.FromRgb(0x27, 0x2D, 0x3C));
                dc.DrawGeometry(pad, new Pen(B(Color.FromArgb(40, 255, 255, 255)), 0.6),
                    Poly(P(x + 115, y + 135, z), P(x + 205, y + 135, z), P(x + 205, y + 200, z), P(x + 115, y + 200, z)));
                break;
            }
            case PartKind.Heatsink:
            {
                var fin = new Pen(B(Color.FromArgb(90, 0, 0, 0)), 0.8);
                for (int i = 1; i < 12; i++) dc.DrawLine(fin, P(x + w * i / 12, y, z), P(x + w * i / 12, y + d, z));
                break;
            }
            case PartKind.Chip when p.Heat == HeatKind.Cpu || p.Heat == HeatKind.Gpu:
            {
                var die = B(Color.FromArgb(70, 0, 0, 0));
                dc.DrawGeometry(die, null, Poly(P(x + w * 0.25, y + d * 0.25, z), P(x + w * 0.75, y + d * 0.25, z), P(x + w * 0.75, y + d * 0.75, z), P(x + w * 0.25, y + d * 0.75, z)));
                break;
            }
        }
    }

    private void DrawScreen(DrawingContext dc, double x, double y, double z, double w, double h)
    {
        // Bildschirmfläche auf der Vorderseite des Deckels
        var bezel = new[] { P(x + 8, y, z + 8), P(x + w - 8, y, z + 8), P(x + w - 8, y, z + h - 8), P(x + 8, y, z + h - 8) };
        var screen = new LinearGradientBrush(Color.FromRgb(0x12, 0x18, 0x2C), Color.FromRgb(0x1C, 0x14, 0x33), 0);
        screen.Freeze();
        dc.DrawGeometry(screen, null, Poly(bezel));
        // kleine Puls-Kurve auf dem Display
        double[,] pts = { { 0.15, 0.5 }, { 0.38, 0.5 }, { 0.45, 0.72 }, { 0.53, 0.25 }, { 0.6, 0.6 }, { 0.65, 0.5 }, { 0.85, 0.5 } };
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            for (int i = 0; i < pts.GetLength(0); i++)
            {
                var pt = P(x + w * pts[i, 0], y, z + h * pts[i, 1]);
                if (i == 0) ctx.BeginFigure(pt, false, false); else ctx.LineTo(pt, true, true);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(B(Color.FromArgb(150, 0x22, 0xD3, 0xEE)), 2.2) { LineJoin = PenLineJoin.Round }, line);
    }

    private void DrawFan(DrawingContext dc, double cx, double cy, double z, double h, double r, Color color, List<Point[]> faces)
    {
        double rx = r * Math.Sqrt(2) * Cos30 * _scale, ry = r * Math.Sqrt(2) * Sin30 * _scale;
        var bottom = P(cx, cy, z);
        var top = P(cx, cy, z + h);
        var side = B(Shade(color, 0.6));
        dc.DrawEllipse(side, null, bottom, rx, ry);
        dc.DrawRectangle(side, null, new Rect(new Point(top.X - rx, top.Y), new Point(bottom.X + rx, bottom.Y)));
        dc.DrawEllipse(B(color), new Pen(B(Color.FromArgb(60, 255, 255, 255)), 0.8), top, rx, ry);

        // Rotorblätter
        var blade = new Pen(B(Color.FromRgb(0x4B, 0x55, 0x63)), Math.Max(1.2, 2.2 * _scale)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 9; i++)
        {
            double a = _fanAngle + i * Math.PI * 2 / 9;
            var inner = P(cx + Math.Cos(a) * r * 0.3, cy + Math.Sin(a) * r * 0.3, z + h);
            var outer = P(cx + Math.Cos(a + 0.5) * r * 0.88, cy + Math.Sin(a + 0.5) * r * 0.88, z + h);
            dc.DrawLine(blade, inner, outer);
        }
        dc.DrawEllipse(B(Color.FromRgb(0x2A, 0x30, 0x40)), null, top, rx * 0.28, ry * 0.28);

        // Trefferfläche: Rechteck um den Zylinder
        faces.Add([new Point(top.X - rx, top.Y - ry), new Point(top.X + rx, top.Y - ry), new Point(bottom.X + rx, bottom.Y + ry), new Point(bottom.X - rx, bottom.Y + ry)]);
    }

    private double? TempOf(Part p) => p.Heat is HeatKind k && _temps.TryGetValue(k, out var t) ? t.Celsius : null;

    private void DrawGlow(DrawingContext dc, Point center, double radius, double celsius, double opacity)
    {
        if (opacity <= 0.02 || radius < 2) return;
        var c = TemperatureService.ColorFor(celsius);
        double intensity = Math.Clamp((celsius - 25) / 65, 0.25, 1);
        byte a0 = (byte)(245 * opacity * Math.Max(0.55, intensity));
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(a0, c.R, c.G, c.B), 0),
                new GradientStop(Color.FromArgb((byte)(a0 * 0.55), c.R, c.G, c.B), 0.45),
                new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        };
        brush.Freeze();
        double r = radius * (0.75 + intensity * 0.5);
        dc.DrawEllipse(brush, null, center, r * 1.22, r * 0.71);
    }

    private (double X, double Y) HeatCenter(Part p) => (p.HeatX ?? p.X + p.W / 2, p.HeatY ?? p.Y + p.D / 2);

    private void DrawPartGlow(DrawingContext dc, Part p, double e)
    {
        if (TempOf(p) is not double t) return;
        var (z, y) = Placed(p, e);
        var (hx, hy) = HeatCenter(p);
        hy += y - p.Y;
        double radius = Math.Min(Math.Max(p.W, p.D), 90) * 0.95 * _scale * p.GlowScale;
        // Teile unter dem Gehäuse sind im zusammengebauten Zustand verdeckt – dort übernimmt der Oberflächen-Hotspot
        double opacity = p.Covered ? Math.Clamp(e * 1.6, 0, 1) : 1;
        DrawGlow(dc, P(hx, hy, z + p.H), radius, t, opacity);
    }

    private void DrawSurfaceHotspots(DrawingContext dc, double e)
    {
        double opacity = Math.Pow(1 - e, 1.5);
        if (opacity <= 0.02) return;
        var cover = _parts.LastOrDefault(p => p.Kind is PartKind.Deck or PartKind.Glass);
        if (cover == null) return;
        var (cz, _) = Placed(cover, e);
        foreach (var p in _parts.Where(p => p.Covered && p.Labeled))
        {
            if (TempOf(p) is not double t) continue;
            var (hx, hy) = HeatCenter(p);
            double radius = Math.Min(Math.Max(p.W, p.D), 90) * 1.25 * _scale * p.GlowScale;
            DrawGlow(dc, P(hx, hy, cz + cover.H), radius, t, opacity);
        }
    }

    private FormattedText Text(string s, double size, Brush brush, FontWeight? weight = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void DrawLabels(DrawingContext dc, double e, Size size)
    {
        var cover = _parts.LastOrDefault(p => p.Kind is PartKind.Deck or PartKind.Glass);
        double coverZ = cover != null ? Placed(cover, e).Z + cover.H : 0;
        var items = new List<(Point Anchor, string Label, TempSensor Sensor)>();
        foreach (var p in _parts.Where(p => p.Labeled && p.Heat is HeatKind k && _temps.ContainsKey(k)))
        {
            var sensor = _temps[p.Heat!.Value];
            var (z, y) = Placed(p, e);
            var (hx, hy) = HeatCenter(p);
            var onPart = P(hx, hy + (y - p.Y), z + p.H);
            var anchor = onPart;
            if (p.Covered)
            {
                var onSurface = P(hx, hy, coverZ);
                double f = Math.Clamp(e * 1.4, 0, 1);
                anchor = new Point(onSurface.X + (onPart.X - onSurface.X) * f, onSurface.Y + (onPart.Y - onSurface.Y) * f);
            }
            items.Add((anchor, p.Label, sensor));
        }

        double colX = size.Width - LabelColumn + 10;
        const double boxH = 40, gap = 6;
        var sorted = items.OrderBy(i => i.Anchor.Y).ToList();
        var ys = new double[sorted.Count];
        double minY = 12;
        for (int i = 0; i < sorted.Count; i++)
        {
            ys[i] = Math.Max(minY, sorted[i].Anchor.Y - boxH / 2);
            minY = ys[i] + boxH + gap;
        }
        // Falls unten übergelaufen: nach oben schieben
        double overflow = (sorted.Count > 0 ? ys[^1] + boxH : 0) - (size.Height - 50);
        if (overflow > 0) for (int i = 0; i < ys.Length; i++) ys[i] = Math.Max(8, ys[i] - overflow);

        var bg = B(Color.FromArgb(235, 0x17, 0x1B, 0x27));
        var border = new Pen(B(Color.FromRgb(0x2C, 0x33, 0x4A)), 1);
        var sub = B(Color.FromRgb(0x8C, 0x94, 0xAA));
        var text = B(Color.FromRgb(0xE7, 0xEA, 0xF3));
        for (int i = 0; i < sorted.Count; i++)
        {
            var (anchor, label, sensor) = sorted[i];
            var color = TemperatureService.ColorFor(sensor.Celsius);
            var cb = B(color);
            var rect = new Rect(colX, ys[i], LabelColumn - 22, boxH);
            var linePen = new Pen(B(color, 150), 1) { DashStyle = new DashStyle([3, 3], 0) };
            dc.DrawLine(linePen, anchor, new Point(rect.Left, rect.Top + boxH / 2));
            dc.DrawEllipse(cb, new Pen(B(Colors.White, 200), 1.2), anchor, 3.5, 3.5);
            dc.DrawRoundedRectangle(bg, border, rect, 8, 8);
            dc.DrawRoundedRectangle(cb, null, new Rect(rect.Left, rect.Top, 4, boxH), 2, 2);
            dc.DrawText(Text(label, 11.5, sub), new Point(rect.Left + 12, rect.Top + 4));
            var val = Text(sensor.ValueText, 15, cb, FontWeights.SemiBold);
            dc.DrawText(val, new Point(rect.Left + 12, rect.Top + 18));
            var lvl = Text(TemperatureService.LevelText(sensor.Level) + (sensor.Estimated ? " · geschätzt" : ""), 10.5, text);
            dc.DrawText(lvl, new Point(rect.Left + 18 + val.Width, rect.Top + 22));
        }
    }

    private void DrawLegend(DrawingContext dc, Size size)
    {
        var rect = new Rect(16, size.Height - 30, 200, 8);
        var (min, max) = TemperatureService.ScaleRange;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        foreach (var (t, c) in TemperatureService.GradientStops) brush.GradientStops.Add(new GradientStop(c, (t - min) / (max - min)));
        brush.Freeze();
        dc.DrawRoundedRectangle(brush, null, rect, 4, 4);
        var sub = B(Color.FromRgb(0x8C, 0x94, 0xAA));
        dc.DrawText(Text($"{min:0} °C", 10.5, sub), new Point(rect.Left, rect.Top - 16));
        var hi = Text($"≥ {max:0} °C", 10.5, sub);
        dc.DrawText(hi, new Point(rect.Right - hi.Width, rect.Top - 16));
    }

    private void DrawHoverInfo(DrawingContext dc, Size size)
    {
        var p = _hover!;
        string title = p.Label;
        string line2, line3;
        if (p.Heat is HeatKind k && _temps.TryGetValue(k, out var s))
        {
            line2 = $"{s.ValueText}  ·  {TemperatureService.LevelText(s.Level)}" + (s.IsThrottling ? "  ·  drosselt!" : "");
            line3 = s.Source;
        }
        else
        {
            line2 = string.IsNullOrEmpty(p.Description) ? "" : p.Description;
            line3 = "Kein Temperatursensor";
        }
        var t1 = Text(title, 13, B(Colors.White), FontWeights.SemiBold);
        var t2 = Text(line2, 12, B(Color.FromRgb(0xE7, 0xEA, 0xF3)));
        var t3 = Text(line3, 11, B(Color.FromRgb(0x8C, 0x94, 0xAA)));
        double w = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 24, h = 68;
        double x = Math.Min(_mouse.X + 16, size.Width - w - 6), y = Math.Min(_mouse.Y + 16, size.Height - h - 6);
        dc.DrawRoundedRectangle(B(Color.FromArgb(245, 0x1E, 0x23, 0x33)), new Pen(B(Color.FromRgb(0x3A, 0x42, 0x60)), 1), new Rect(x, y, w, h), 8, 8);
        dc.DrawText(t1, new Point(x + 12, y + 8));
        dc.DrawText(t2, new Point(x + 12, y + 28));
        dc.DrawText(t3, new Point(x + 12, y + 46));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        Part? hit = null;
        for (int i = _hitFaces.Count - 1; i >= 0 && hit == null; i--)
        {
            // Durch das Glas-Seitenteil hindurch auf die Bauteile zeigen
            if (_hitFaces[i].Part.Kind == PartKind.Glass && Explode < 0.5) continue;
            foreach (var f in _hitFaces[i].Faces)
                if (Poly(f).FillContains(_mouse)) { hit = _hitFaces[i].Part; break; }
        }
        _hover = hit;
        Cursor = hit != null ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }
}
