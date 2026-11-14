using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BoardSim.Core.Analysis;
using BoardSim.Core.Emulation;
using BoardSim.Core.Model;

namespace BoardSim.App.Controls;

/// <summary>A probe clipped onto a net; channels 1-4 as on a bench scope.</summary>
public sealed record Probe(int Channel, Net Net, Pad Pad)
{
    public static readonly Color[] Colors =
    {
        Color.Parse("#ffd60a"), Color.Parse("#3bd1ff"), Color.Parse("#ff4fd8"), Color.Parse("#41d98a"),
    };
    public Color Color => Colors[(Channel - 1) % Colors.Length];
}

/// <summary>Run: the chip's own time, triggered. Roll: wall-clock time, the last seconds.</summary>
public enum ScopeMode { Run, Roll }

/// <summary>
/// A four-channel oscilloscope. Each probe shows what reaches its net: GPIO
/// edges from the firmware (at the voltage the net really has), LED data from
/// the RMT, a crystal's sine, or a steady level - with frequency, duty cycle
/// and levels measured, and an AUTO per channel that picks the time base and
/// triggers on it.
/// </summary>
public sealed class ScopeView : Control
{
    private static readonly Typeface Font = new(new FontFamily("Inter, Segoe UI, Helvetica, sans-serif"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Mono = new(new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace"));
    public static readonly double[] Steps = BuildSteps();
    private const double LabelWidth = 236;
    private readonly List<(Rect Area, Probe Probe)> _autoButtons = new();

    public IReadOnlyList<Probe> Probes { get; set; } = Array.Empty<Probe>();
    /// <summary>What each probe sees in Run mode.</summary>
    public Func<Probe, ScopeTrace?>? Trace { get; set; }
    /// <summary>Wall-clock history for Roll mode: (seconds, volts or NaN).</summary>
    public Func<Net, IReadOnlyList<(double T, double V)>>? History { get; set; }
    public Func<Net, double?>? Now { get; set; }
    public ScopeMode Mode { get; set; } = ScopeMode.Run;
    public int StepIndex { get; set; } = Array.IndexOf(Steps, 1e-6);
    public int RollStepIndex { get; set; } = Array.IndexOf(Steps, 0.5);
    /// <summary>Channel the Run view triggers on (rising edge); 0 = none.</summary>
    public int TriggerChannel { get; set; }
    public double PerDiv => Mode == ScopeMode.Run ? Steps[StepIndex] : Steps[RollStepIndex];

    public event Action<Probe>? AutoRequested;

    private static double[] BuildSteps()
    {
        var list = new List<double>();
        for (var e = -8; e <= 0; e++)
            foreach (var m in new[] { 1.0, 2, 5 })
                list.Add(m * Math.Pow(10, e));
        list.Add(1); list.Add(2);
        return list.Distinct().OrderBy(x => x).ToArray();
    }

    public void Zoom(int delta)
    {
        if (Mode == ScopeMode.Run) StepIndex = Math.Clamp(StepIndex + delta, 0, Steps.Length - 1);
        else RollStepIndex = Math.Clamp(RollStepIndex + delta, Array.IndexOf(Steps, 0.05), Steps.Length - 1);
        InvalidateVisual();
    }

    public void SetPerDiv(double seconds)
    {
        var best = 0;
        for (var i = 0; i < Steps.Length; i++) if (Steps[i] <= seconds * 1.0001) best = i;
        StepIndex = best;
        InvalidateVisual();
    }

    public static string FormatTime(double s) => s switch
    {
        >= 1 => $"{s:0.##} s",
        >= 1e-3 => $"{s * 1e3:0.##} ms",
        >= 1e-6 => $"{s * 1e6:0.##} µs",
        _ => $"{s * 1e9:0.##} ns",
    };

    public static string FormatHz(double hz) => hz switch
    {
        >= 1e6 => $"{hz / 1e6:0.###} MHz",
        >= 1e3 => $"{hz / 1e3:0.##} kHz",
        _ => $"{hz:0.##} Hz",
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);
        foreach (var (area, probe) in _autoButtons)
            if (area.Contains(p)) { AutoRequested?.Invoke(probe); e.Handled = true; return; }
    }

    public override void Render(DrawingContext ctx)
    {
        _autoButtons.Clear();
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#05080b")), new Pen(new SolidColorBrush(Color.Parse("#2a313d")), 1), bounds, 12, 12);
        const double right = 14, top = 10, bottom = 24;
        var screen = new Rect(LabelWidth, top, Math.Max(10, bounds.Width - LabelWidth - right), Math.Max(10, bounds.Height - top - bottom));

        var grid = new Pen(new SolidColorBrush(Color.Parse("#1b2a33")), 1);
        var sub = new Pen(new SolidColorBrush(Color.Parse("#122028")), 1, new DashStyle(new double[] { 1, 3 }, 0));
        for (var i = 0; i <= 10; i++)
        {
            var x = screen.X + screen.Width * i / 10;
            ctx.DrawLine(i is 0 or 10 ? grid : sub, new Point(x, screen.Y), new Point(x, screen.Bottom));
        }
        ctx.DrawRectangle(null, grid, screen);

        if (Probes.Count == 0)
        {
            var hint = Text("Turn on Probe and click pads on the board to clip probes on (up to 4).", 13, "#6b7383", Font);
            ctx.DrawText(hint, new Point(screen.X + (screen.Width - hint.Width) / 2, screen.Y + (screen.Height - hint.Height) / 2));
            return;
        }

        var window = PerDiv * 10;
        var traces = Probes.Select(p => Mode == ScopeMode.Run ? Trace?.Invoke(p) : null).ToList();
        var start = Mode == ScopeMode.Run ? WindowStart(traces, window) : 0;
        var laneH = screen.Height / Probes.Count;

        for (var lane = 0; lane < Probes.Count; lane++)
        {
            var p = Probes[lane];
            var trace = traces[lane];
            var y0 = screen.Y + lane * laneH;
            if (lane > 0) ctx.DrawLine(grid, new Point(screen.X, y0), new Point(screen.Right, y0));
            double Y(double v) => y0 + laneH - 6 - Math.Clamp(v / 5.5, 0, 1.05) * (laneH - 14);
            ctx.DrawLine(sub, new Point(screen.X, Y(0)), new Point(screen.Right, Y(0)));

            var points = Mode == ScopeMode.Roll ? RollPoints(p, window) : RunPoints(p, trace, start, window, screen.Width);
            var color = p.Color;
            if (points.Count > 1)
            {
                var g = new StreamGeometry();
                using (var gc = g.Open())
                {
                    var first = true;
                    foreach (var (t, v) in points)
                    {
                        var x = screen.X + Math.Clamp(t / window, 0, 1) * screen.Width;
                        var pt = new Point(x, double.IsNaN(v) ? Y(1.6) : Y(v));
                        if (first) { gc.BeginFigure(pt, false); first = false; } else gc.LineTo(pt);
                    }
                    gc.EndFigure(false);
                }
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color, 0.18), 5, lineJoin: PenLineJoin.Round), g);
                ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color), 1.8), g);
            }

            if (Mode == ScopeMode.Run && TriggerChannel == p.Channel)
            {
                var tx = screen.X + screen.Width * 0.1;
                ctx.DrawLine(new Pen(new SolidColorBrush(color, 0.5), 1, new DashStyle(new double[] { 3, 3 }, 0)), new Point(tx, y0 + 2), new Point(tx, y0 + laneH - 2));
            }

            DrawLabel(ctx, p, trace, y0, laneH);
        }

        if (Mode == ScopeMode.Run) DrawBits(ctx, traces, start, window, screen, laneH);

        var axis = Mode == ScopeMode.Roll
            ? $"Roll · {FormatTime(PerDiv)}/div · last {FormatTime(window)} of wall-clock time"
            : $"Run · {FormatTime(PerDiv)}/div · chip time" + (TriggerChannel > 0 ? $" · trigger CH{TriggerChannel} rising" : " · free running");
        ctx.DrawText(Text(axis, 11, "#8b93a1", Font), new Point(screen.X, screen.Bottom + 5));
        var end = Text(Mode == ScopeMode.Roll ? "now" : "+" + FormatTime(window * 0.9), 11, "#5b6270", Mono);
        ctx.DrawText(end, new Point(screen.Right - end.Width, screen.Bottom + 5));
    }

    private void DrawLabel(DrawingContext ctx, Probe p, ScopeTrace? trace, double y0, double laneH)
    {
        var color = p.Color;
        var mid = y0 + laneH / 2;
        ctx.DrawRectangle(new SolidColorBrush(color), null, new Rect(12, mid - 11, 30, 22), 5, 5);
        var ch = Text($"{p.Channel}", 12, "#05080b", Font);
        ctx.DrawText(ch, new Point(27 - ch.Width / 2, mid - ch.Height / 2));

        // Three lines left of the AUTO button: name, frequency, levels and source.
        const double textX = 50;
        var auto = new Rect(LabelWidth - 58, mid - 25, 46, 18);
        var textWidth = auto.X - textX - 6;
        var fullWidth = LabelWidth - textX - 10;

        string line1, line2;
        if (Mode == ScopeMode.Roll)
        {
            (line1, line2) = RollMeasure(p);
        }
        else
        {
            var m = Measurement.Of(trace ?? new DcTrace(Now?.Invoke(p.Net)));
            line1 = m.Hz is { } f ? FormatHz(f) + (m.DutyPercent is { } d ? $" · {d:0}% duty" : "")
                  : m.Max is { } v ? $"DC {v:0.00} V" : "floating";
            line2 = m.Hz is not null && m.Min is { } lo && m.Max is { } hi ? $"{lo:0.00}-{hi:0.00} V" : "";
            if (trace is not null && trace.Source.Length > 0) line2 += (line2.Length > 0 ? " · " : "") + trace.Source;
        }

        ctx.DrawText(Fit(p.Net.DisplayName, 12, "#e6e9ef", Font, textWidth), new Point(textX, mid - 25));
        ctx.DrawText(Fit(line1, 11, "#e6e9ef", Mono, fullWidth), new Point(textX, mid - 7));
        if (line2.Length > 0) ctx.DrawText(Fit(line2, 10, "#7d8594", Font, fullWidth), new Point(textX, mid + 9));

        var on = Mode == ScopeMode.Run && TriggerChannel == p.Channel;
        ctx.DrawRectangle(new SolidColorBrush(on ? color : Color.Parse("#1b2029"), on ? 0.25 : 1),
            new Pen(new SolidColorBrush(on ? color : Color.Parse("#2f3744")), 1), auto, 5, 5);
        var at = Text("AUTO", 10, on ? "#ffffff" : "#c6ccd6", Font);
        ctx.DrawText(at, new Point(auto.X + (auto.Width - at.Width) / 2, auto.Y + (auto.Height - at.Height) / 2));
        _autoButtons.Add((auto, p));
    }

    /// <summary>Text cut with an ellipsis so it fits <paramref name="width"/>.</summary>
    private static FormattedText Fit(string s, double size, string color, Typeface face, double width)
    {
        var t = Text(s, size, color, face);
        if (t.Width <= width) return t;
        for (var n = s.Length - 1; n > 1; n--)
        {
            t = Text(s[..n] + "…", size, color, face);
            if (t.Width <= width) return t;
        }
        return t;
    }

    /// <summary>Chip time where the screen starts: just before the trigger channel's latest full-screen rising edge.</summary>
    private double WindowStart(List<ScopeTrace?> traces, double window)
    {
        var index = Probes.ToList().FindIndex(p => p.Channel == TriggerChannel);
        var trace = index >= 0 ? traces[index] : null;
        var windowNs = window * 1e9;
        switch (trace)
        {
            case EdgeTrace e:
            {
                long? best = null;
                for (var i = e.Edges.Count - 1; i > 0; i--)
                {
                    if (!e.Edges[i].High || e.Edges[i - 1].High) continue;
                    best ??= e.Edges[i].T;
                    if (e.Edges[i].T + windowNs * 0.9 <= e.NowNs) { best = e.Edges[i].T; break; }
                }
                return best is { } t ? t / 1e9 - window * 0.1 : e.NowNs / 1e9 - window;
            }
            case RmtTrace r:
                return RmtStart(r) / 1e9 - window * 0.1;
        }
        var now = traces.OfType<EdgeTrace>().Select(e => e.NowNs).DefaultIfEmpty(0).Max();
        var rmt = traces.OfType<RmtTrace>().FirstOrDefault();
        if (rmt is not null && (now == 0 || TriggerChannel == 0)) return RmtStart(rmt) / 1e9 - window * 0.1;
        return now / 1e9 - window;
    }

    private static double RmtStart(RmtTrace r)
    {
        var total = r.Frame.Items.Sum(i => (double)((i & 0x7fff) + ((i >> 16) & 0x7fff))) * r.Frame.TickNs;
        return (r.Frame.EndNs ?? (long)total) - total;
    }

    private List<(double T, double V)> RunPoints(Probe p, ScopeTrace? trace, double start, double window, double widthPx)
    {
        var pts = new List<(double, double)>();
        switch (trace)
        {
            case EdgeTrace e:
            {
                var startNs = start * 1e9;
                var endNs = startNs + window * 1e9;
                bool? level = null;
                foreach (var (t, high) in e.Edges)
                {
                    if (t <= startNs) { level = high; continue; }
                    if (t > endNs) break;
                    var x = (t - startNs) / 1e9;
                    var before = level ?? !high;
                    if (pts.Count == 0) pts.Add((0, before ? e.High : e.Low));
                    pts.Add((x, before ? e.High : e.Low));
                    pts.Add((x, high ? e.High : e.Low));
                    level = high;
                }
                if (level is null) return pts;
                if (pts.Count == 0) pts.Add((0, level.Value ? e.High : e.Low));
                var lastX = Math.Min(window, (e.NowNs - startNs) / 1e9);
                pts.Add((Math.Max(lastX, pts[^1].Item1), level.Value ? e.High : e.Low));
                return pts;
            }
            case RmtTrace r:
            {
                var t = RmtStart(r) / 1e9 - start;
                var frameEnd = t + r.Frame.Items.Sum(i => (double)((i & 0x7fff) + ((i >> 16) & 0x7fff))) * r.Frame.TickNs * 1e-9;
                if (t > window || frameEnd < 0)
                {
                    // The last frame is outside the screen: the line idles low.
                    pts.Add((0, r.Low)); pts.Add((window, r.Low));
                    return pts;
                }
                if (t > 0) pts.Add((0, r.Low));
                pts.Add((Math.Max(0, t), r.Low));
                foreach (var item in r.Frame.Items)
                {
                    if (t > window) break;
                    var (hi, lo) = Measurement.ItemHighLow(item);
                    var th = hi * r.Frame.TickNs * 1e-9;
                    var tl = lo * r.Frame.TickNs * 1e-9;
                    if (t + th + tl >= 0)
                    {
                        pts.Add((Math.Max(0, t), r.Low)); pts.Add((Math.Max(0, t), r.High));
                        pts.Add((Math.Max(0, t + th), r.High)); pts.Add((Math.Max(0, t + th), r.Low));
                    }
                    t += th + tl;
                }
                pts.Add((window, r.Low));
                return pts;
            }
            case SineTrace s when window * s.Hz > widthPx / 2:
            {
                // Far more periods than pixels: a real scope shows a solid band.
                for (var i = 0; i <= (int)widthPx; i++)
                {
                    var t = window * i / widthPx;
                    pts.Add((t, i % 2 == 0 ? s.Max : s.Min));
                }
                return pts;
            }
            case SineTrace s:
            {
                var periods = window * s.Hz;
                var n = (int)Math.Clamp(periods * 24, 64, Math.Max(64, widthPx * 2));
                var mid = (s.Max + s.Min) / 2;
                var amp = (s.Max - s.Min) / 2;
                for (var i = 0; i <= n; i++)
                {
                    var t = window * i / n;
                    pts.Add((t, mid + amp * Math.Sin(2 * Math.PI * s.Hz * t)));
                }
                return pts;
            }
            default:
            {
                var v = Now?.Invoke(p.Net) ?? double.NaN;
                pts.Add((0, v)); pts.Add((window, v));
                return pts;
            }
        }
    }

    private void DrawBits(DrawingContext ctx, List<ScopeTrace?> traces, double start, double window, Rect screen, double laneH)
    {
        for (var lane = 0; lane < traces.Count; lane++)
        {
            if (traces[lane] is not RmtTrace r) continue;
            if (1.25e-6 / window * screen.Width < 14) return;
            var t = RmtStart(r) / 1e9 - start;
            foreach (var item in r.Frame.Items)
            {
                var (hi, lo) = Measurement.ItemHighLow(item);
                var dur = (hi + lo) * r.Frame.TickNs * 1e-9;
                if (t > window) break;
                if (t >= 0 && lo * r.Frame.TickNs < 5000)
                {
                    var bt = Text(hi * r.Frame.TickNs > 600 ? "1" : "0", 10, "#c6ccd6", Mono);
                    ctx.DrawText(bt, new Point(screen.X + (t + dur / 2) / window * screen.Width - bt.Width / 2, screen.Y + lane * laneH + 2));
                }
                t += dur;
            }
            return;
        }
    }

    private List<(double T, double V)> RollPoints(Probe p, double window)
    {
        var points = new List<(double, double)>();
        var history = History?.Invoke(p.Net);
        if (history is null || history.Count == 0) return points;
        var start = Environment.TickCount64 / 1000.0 - window;
        double? last = null;
        foreach (var (t, v) in history)
        {
            if (t < start) { last = v; continue; }
            var x = t - start;
            if (last is { } lv) points.Add((x, lv));
            else if (points.Count == 0) points.Add((x, v));
            points.Add((x, v));
            last = v;
        }
        if (points.Count == 0 && last is { } only) { points.Add((0, only)); points.Add((window, only)); }
        else if (last is { } lv2) points.Add((window, lv2));
        return points;
    }

    /// <summary>Frequency from wall-clock history: signals driven from outside (a Pi, a script).</summary>
    private (string, string) RollMeasure(Probe p)
    {
        var h = History?.Invoke(p.Net);
        var now = Now?.Invoke(p.Net);
        if (h is { Count: >= 3 })
        {
            var rises = new List<double>();
            for (var i = 1; i < h.Count; i++)
                if (h[i].V > 2.0 && !(h[i - 1].V > 2.0)) rises.Add(h[i].T);
            if (rises.Count >= 3)
            {
                var periods = rises.Zip(rises.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
                return (FormatHz(1 / periods[periods.Count / 2]), $"now {now:0.00} V · wall clock");
            }
        }
        return (now is { } v ? $"DC {v:0.00} V" : "floating", "");
    }

    private static FormattedText Text(string s, double size, string color, Typeface face) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(Color.Parse(color)));
}
