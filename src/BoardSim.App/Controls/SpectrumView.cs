using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BoardSim.Core.Emc;

namespace BoardSim.App.Controls;

/// <summary>
/// The radiated emission estimate as an EMC receiver would plot it: dBµV/m at
/// 3 m from 30 MHz to 1 GHz on a log axis, with the CISPR 32 class B limit,
/// the whole board, and the selected net split into differential mode (the
/// loop on the board) and common mode (its cable).
/// </summary>
public sealed class SpectrumView : Control
{
    private static readonly Typeface Font = new(new FontFamily("Inter, Segoe UI, Helvetica, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);
    private const double MinHz = 30e6, MaxHz = 1e9, MinDb = -10, MaxDb = 70;

    public EmcAnalysis? Analysis { get; set; }
    public ActiveNet? Selected { get; set; }

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        ctx.FillRectangle(new SolidColorBrush(Color.Parse("#0b0e13")), new Rect(0, 0, b.Width, b.Height), 10);
        var plot = new Rect(44, 12, Math.Max(10, b.Width - 56), Math.Max(10, b.Height - 40));
        double X(double hz) => plot.X + Math.Log(hz / MinHz) / Math.Log(MaxHz / MinHz) * plot.Width;
        double Y(double db) => plot.Bottom - (Math.Clamp(db, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb) * plot.Height;

        var grid = new Pen(new SolidColorBrush(Color.Parse("#1d232d")), 1);
        foreach (var db in new[] { 0.0, 20, 40, 60 })
        {
            ctx.DrawLine(grid, new Point(plot.X, Y(db)), new Point(plot.Right, Y(db)));
            Label(ctx, db.ToString("0", CultureInfo.InvariantCulture), new Point(8, Y(db) - 7), "#6b7380");
        }
        foreach (var hz in new[] { 30e6, 50e6, 100e6, 200e6, 500e6, 1e9 })
        {
            ctx.DrawLine(grid, new Point(X(hz), plot.Y), new Point(X(hz), plot.Bottom));
            Label(ctx, hz >= 1e9 ? "1 GHz" : $"{hz / 1e6:0} MHz", new Point(X(hz) - 18, plot.Bottom + 6), "#6b7380");
        }

        // CISPR 32 class B, 3 m.
        var limit = new Pen(new SolidColorBrush(Color.Parse("#ff5d5d")), 2, new DashStyle(new double[] { 4, 3 }, 0));
        ctx.DrawLine(limit, new Point(X(MinHz), Y(40)), new Point(X(230e6), Y(40)));
        ctx.DrawLine(limit, new Point(X(230e6), Y(40)), new Point(X(230e6), Y(47)));
        ctx.DrawLine(limit, new Point(X(230e6), Y(47)), new Point(X(MaxHz), Y(47)));
        Label(ctx, "CISPR 32 class B", new Point(X(240e6), Y(47) - 18), "#ff8a8a");

        if (Analysis is { } emc)
        {
            Curve(ctx, emc.Frequencies.Select(f => (f, emc.Total(f))), "#cfd6e2", 1.5, X, Y);
            if (Selected is { } s)
            {
                Curve(ctx, emc.Frequencies.Select(f => (f, emc.DifferentialMode(s, f))), "#3bd1ff", 1.2, X, Y, dashed: true);
                if (s.LeavesBoard) Curve(ctx, emc.Frequencies.Select(f => (f, emc.CommonMode(s, f))), "#ffd60a", 1.2, X, Y, dashed: true);
                Curve(ctx, emc.Frequencies.Select(f => (f, emc.Total(s, f))), "#3bd1ff", 2.2, X, Y);
            }
            var lx = plot.X + 8;
            Label(ctx, "all nets", new Point(lx, plot.Y + 4), "#cfd6e2");
            if (Selected is not null)
            {
                Label(ctx, $"{Selected.Net.DisplayName}: loop (dashed)", new Point(lx + 70, plot.Y + 4), "#3bd1ff");
                if (Selected.LeavesBoard) Label(ctx, "cable (dashed)", new Point(lx + 70, plot.Y + 20), "#ffd60a");
            }
        }
        Label(ctx, "dBµV/m at 3 m · estimate", new Point(plot.Right - 150, plot.Bottom - 18), "#6b7380");
    }

    private static void Curve(DrawingContext ctx, IEnumerable<(double Hz, double Db)> points, string color,
        double width, Func<double, double> x, Func<double, double> y, bool dashed = false)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse(color)), width, dashed ? new DashStyle(new double[] { 3, 3 }, 0) : null);
        Point? last = null;
        foreach (var (hz, db) in points)
        {
            if (double.IsNegativeInfinity(db) || db < MinDb) { last = null; continue; }
            var p = new Point(x(hz), y(db));
            if (last is { } l) ctx.DrawLine(pen, l, p);
            last = p;
        }
    }

    private static void Label(DrawingContext ctx, string text, Point at, string color) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 10,
            new SolidColorBrush(Color.Parse(color))), at);
}
