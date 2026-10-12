using System.Windows;
using System.Windows.Media;

namespace PerformanceMonitor.Controls;

/// <summary>Leichtgewichtiges Live-Liniendiagramm mit Verlaufsfüllung und optionaler zweiter Datenreihe.</summary>
public sealed class LiveChart : FrameworkElement
{
    private readonly List<double> _primary = new();
    private readonly List<double> _secondary = new();

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryStrokeProperty = DependencyProperty.Register(
        nameof(SecondaryStroke), typeof(Brush), typeof(LiveChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LiveChart),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush SecondaryStroke { get => (Brush)GetValue(SecondaryStrokeProperty); set => SetValue(SecondaryStrokeProperty, value); }

    /// <summary>Oberer Wert der Skala. <see cref="double.NaN"/> = automatisch.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public int Capacity { get; set; } = 60;

    private static readonly Pen GridPen = CreateGridPen();

    private static Pen CreateGridPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1) { DashStyle = new DashStyle([3, 4], 0) };
        pen.Freeze();
        return pen;
    }

    public void Push(double value, double? secondary = null)
    {
        _primary.Add(double.IsNaN(value) ? 0 : value);
        if (_primary.Count > Capacity) _primary.RemoveAt(0);
        if (secondary.HasValue)
        {
            _secondary.Add(secondary.Value);
            if (_secondary.Count > Capacity) _secondary.RemoveAt(0);
        }
        if (IsVisible) InvalidateVisual();
    }

    public void Clear()
    {
        _primary.Clear();
        _secondary.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        for (int i = 1; i <= 3; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        double max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            double m = 0;
            foreach (var v in _primary) m = Math.Max(m, v);
            foreach (var v in _secondary) m = Math.Max(m, v);
            max = Math.Max(1, m * 1.2);
        }

        DrawSeries(dc, _secondary, SecondaryStroke, max, w, h, fill: false);
        DrawSeries(dc, _primary, Stroke, max, w, h, fill: true);
    }

    private void DrawSeries(DrawingContext dc, List<double> values, Brush stroke, double max, double w, double h, bool fill)
    {
        if (values.Count < 2) return;
        double step = w / Math.Max(1, Capacity - 1);
        double x0 = w - (values.Count - 1) * step;
        const double top = 2;

        var points = new Point[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            double v = Math.Clamp(values[i] / max, 0, 1);
            points[i] = new Point(x0 + i * step, top + (h - top) * (1 - v));
        }

        if (fill)
        {
            var color = (stroke as SolidColorBrush)?.Color ?? Colors.White;
            var brush = new LinearGradientBrush(Color.FromArgb(80, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
            brush.Freeze();
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h), true, true);
                ctx.PolyLineTo(points, false, true);
                ctx.LineTo(new Point(points[^1].X, h), false, false);
            }
            area.Freeze();
            dc.DrawGeometry(brush, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points[1..], true, true);
        }
        line.Freeze();
        var pen = new Pen(stroke, 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Punkt am aktuellen Wert
        dc.DrawEllipse(stroke, null, points[^1], 3, 3);
    }
}
