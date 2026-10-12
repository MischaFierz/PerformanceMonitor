using System.Windows.Controls;
using System.Windows.Media;

namespace PerformanceMonitor.Controls;

public partial class MetricCard : UserControl
{
    public MetricCard()
    {
        InitializeComponent();
    }

    public string Title { get => TitleText.Text; set => TitleText.Text = value; }
    public string Glyph { get => GlyphText.Text; set => GlyphText.Text = value; }
    public string Subtitle { get => SubtitleText.Text; set => SubtitleText.Text = value; }
    public string Value { get => ValueText.Text; set => ValueText.Text = value; }
    public string Detail { get => DetailText.Text; set => DetailText.Text = value; }
    public double ChartMaximum { get => Chart.Maximum; set => Chart.Maximum = value; }

    public Brush Accent
    {
        get => Chart.Stroke;
        set
        {
            Chart.Stroke = value;
            GlyphText.Foreground = value;
            if (value is SolidColorBrush b)
                GlyphBorder.Background = new SolidColorBrush(Color.FromArgb(0x26, b.Color.R, b.Color.G, b.Color.B));
        }
    }

    public Brush SecondaryAccent { get => Chart.SecondaryStroke; set => Chart.SecondaryStroke = value; }

    public void Push(double value, double? secondary = null) => Chart.Push(value, secondary);
}
