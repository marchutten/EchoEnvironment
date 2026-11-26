using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EchoEnvironment;

/// <summary>
/// Scrolling time plot of the nine raw IMU counts. Three stacked panels (gyro, accel, mag), each
/// with its X/Y/Z channels, auto-scaled to the visible history.
/// </summary>
public sealed class ImuChart : FrameworkElement
{
    private const int Capacity = 300;
    private static readonly string[] PanelNames = ["Gyro", "Accel", "Mag"];

    // Fixed +/- Y range per panel in raw counts, matching Firmware.cpp settings.
    // Gyro: ITG3200 +/-2000 deg/s at 14.375 LSB/deg/s, 4000 counts is ~280 deg/s (hand motion).
    // Accel: BMA180 +/-2 g, 14-bit, 4096 LSB/g, so +/-2 g = +/-8192.
    // Mag: HMC5883L gain 1.3 Ga = 1090 LSB/Gauss; earth field (~0.5 G) is ~550 counts.
    private static readonly int[] PanelRanges = [4000, 8192, 1000];
    private static readonly Pen[] AxisPens = [CreatePen(Colors.OrangeRed), CreatePen(Colors.LimeGreen), CreatePen(Colors.DeepSkyBlue)];
    private static readonly Pen ZeroPen = CreatePen(Color.FromRgb(70, 70, 70));
    private static readonly Brush Background = new SolidColorBrush(Color.FromRgb(16, 16, 16));
    private static readonly Typeface LabelFace = new("Consolas");

    private readonly int[,] _samples = new int[Capacity, 9];
    private int _count;
    private int _next;

    static ImuChart()
    {
        Background.Freeze();
    }

    public void Add(ReadOnlySpan<int> imu)
    {
        for (int i = 0; i < 9; i++)
        {
            _samples[_next, i] = imu[i];
        }

        _next = (_next + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        InvalidateVisual();
    }

    public void Clear()
    {
        _count = 0;
        _next = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double width = ActualWidth;
        double height = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double panelWidth = width / 3;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int panel = 0; panel < 3; panel++)
        {
            DrawPanel(dc, panel, new Rect(panel * panelWidth + 4, 2, panelWidth - 8, height - 4), dpi);
        }
    }

    private void DrawPanel(DrawingContext dc, int panel, Rect area, double dpi)
    {
        int first = panel * 3;
        int max = PanelRanges[panel];
        int min = -max;

        double range = max - min;
        double Y(int v) => area.Bottom - (Math.Clamp(v, min, max) - min) / range * area.Height;

        dc.DrawLine(ZeroPen, new Point(area.Left, Y(0)), new Point(area.Right, Y(0)));

        if (_count > 1)
        {
            double step = area.Width / (Capacity - 1);
            double x0 = area.Right - (_count - 1) * step;
            for (int c = 0; c < 3; c++)
            {
                var geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(x0, Y(_samples[Index(0), first + c])), false, false);
                    for (int s = 1; s < _count; s++)
                    {
                        ctx.LineTo(new Point(x0 + s * step, Y(_samples[Index(s), first + c])), true, false);
                    }
                }

                geometry.Freeze();
                dc.DrawGeometry(null, AxisPens[c], geometry);
            }
        }

        string label = _count == 0
            ? PanelNames[panel]
            : string.Format(CultureInfo.InvariantCulture, "{0}  x {1}  y {2}  z {3}   [{4}..{5}]",
                PanelNames[panel],
                _samples[Index(_count - 1), first], _samples[Index(_count - 1), first + 1], _samples[Index(_count - 1), first + 2],
                min, max);
        dc.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 11, Brushes.White, dpi),
            new Point(area.Left + 2, area.Top));
    }

    private int Index(int sample) => (_next - _count + sample + Capacity) % Capacity;

    private static Pen CreatePen(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }
}
