using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Chronos.App.Controls;

/// <summary>Draws the hourglass for <see cref="Progress"/>; a transition on Progress moves the sand smoothly.</summary>
public sealed class Hourglass : Control
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<Hourglass, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> SandProperty =
        AvaloniaProperty.Register<Hourglass, IBrush?>(nameof(Sand));

    public static readonly StyledProperty<IBrush?> GlassProperty =
        AvaloniaProperty.Register<Hourglass, IBrush?>(nameof(Glass));

    public static readonly StyledProperty<IBrush?> FrameProperty =
        AvaloniaProperty.Register<Hourglass, IBrush?>(nameof(Frame));

    private const int Steps = 32;

    static Hourglass() => AffectsRender<Hourglass>(ProgressProperty, SandProperty, GlassProperty, FrameProperty);

    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public IBrush? Sand { get => GetValue(SandProperty); set => SetValue(SandProperty, value); }

    public IBrush? Glass { get => GetValue(GlassProperty); set => SetValue(GlassProperty, value); }

    public IBrush? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var rim = Math.Max(2, h * 0.035);
        var bulb = (h - 2 * rim) / 2;
        var centre = w / 2;
        var widest = w / 2 * 0.86;
        var neckY = rim + bulb;

        // y of a point d (share of the bulb) away from the neck; up = -1, down = +1.
        double Y(double d, int side) => neckY + side * d * bulb;
        double X(double d, int edge) => centre + edge * HourglassShape.HalfWidth(d) * widest;

        var p = Progress;
        var top = HourglassShape.TopSand(p);
        var bottom = HourglassShape.BottomSand(p);

        context.DrawGeometry(Sand, null, Band(0, top, -1));
        context.DrawGeometry(Sand, null, Band(1 - bottom, 1, +1));

        if (p is > 0 and < 1)
        {
            var stream = new Pen(Sand, Math.Max(1, w * 0.015));
            context.DrawLine(stream, new Point(centre, neckY), new Point(centre, Y(1 - bottom, +1)));
        }

        var glass = new Pen(Glass, Math.Max(1, w * 0.02));
        context.DrawGeometry(null, glass, Outline(-1));
        context.DrawGeometry(null, glass, Outline(+1));

        var frame = new Pen(Frame, rim);
        context.DrawLine(frame, new Point(0, rim / 2), new Point(w, rim / 2));
        context.DrawLine(frame, new Point(0, h - rim / 2), new Point(w, h - rim / 2));

        StreamGeometry Band(double from, double to, int side)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            g.BeginFigure(new Point(X(from, -1), Y(from, side)), true);
            for (var i = 0; i <= Steps; i++)
            {
                var d = from + (to - from) * i / Steps;
                g.LineTo(new Point(X(d, -1), Y(d, side)));
            }

            for (var i = Steps; i >= 0; i--)
            {
                var d = from + (to - from) * i / Steps;
                g.LineTo(new Point(X(d, +1), Y(d, side)));
            }

            g.EndFigure(true);
            return geometry;
        }

        StreamGeometry Outline(int side)
        {
            var geometry = new StreamGeometry();
            using var g = geometry.Open();
            g.BeginFigure(new Point(X(1, -1), Y(1, side)), false);
            for (var i = Steps; i >= 0; i--) g.LineTo(new Point(X((double)i / Steps, -1), Y((double)i / Steps, side)));
            for (var i = 0; i <= Steps; i++) g.LineTo(new Point(X((double)i / Steps, +1), Y((double)i / Steps, side)));
            g.EndFigure(false);
            return geometry;
        }
    }
}
