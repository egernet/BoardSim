using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BoardSim.App.Rendering;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;
using BoardSim.Core.Simulation;
using AGeometry = Avalonia.Media.Geometry;

namespace BoardSim.App.Controls;

public enum ViewMode { Realistic, Layers }

/// <summary>Draws a board, lets the user pan, zoom and pick pads, and highlights nets.</summary>
public sealed class BoardView : Control
{
    // Realistic colours: green solder mask over FR4, ENIG pads, white silkscreen.
    private static readonly IBrush Backdrop = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#151a22"), 0), new GradientStop(Color.Parse("#0b0e13"), 1) },
    };
    private static readonly IBrush Mask = new SolidColorBrush(Color.Parse("#0f5530"));
    private static readonly IBrush CopperUnderMask = new SolidColorBrush(Color.Parse("#2a8a4c"));
    private static readonly IBrush Gold = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#f3d27a"), 0), new GradientStop(Color.Parse("#c99a3b"), 0.55), new GradientStop(Color.Parse("#e8c46a"), 1) },
    };
    private static readonly IBrush HoleBrush = new SolidColorBrush(Color.Parse("#07090b"));
    private static readonly IBrush Silk = new SolidColorBrush(Color.Parse("#f1f0e6"));
    private static readonly IPen EdgePen = new Pen(new SolidColorBrush(Color.Parse("#d8c58f")), 0.15);
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#000000"), 0.55);
    private static readonly Color Accent = Color.Parse("#3bd1ff");
    private static readonly Typeface SilkFont = new(new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface LabelFont = new(new FontFamily("Inter, Segoe UI, Helvetica, sans-serif"), FontStyle.Normal, FontWeight.Medium);

    private static readonly Dictionary<string, Color> LayerColors = new()
    {
        ["F.Cu"] = Color.Parse("#e04848"), ["B.Cu"] = Color.Parse("#4d7fe6"),
        ["In1.Cu"] = Color.Parse("#d6c447"), ["In2.Cu"] = Color.Parse("#c25bd6"),
        ["In3.Cu"] = Color.Parse("#4fc7b0"), ["In4.Cu"] = Color.Parse("#e0904a"),
    };

    private BoardScene? _scene;
    private Matrix _view = Matrix.Identity;
    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panStart;
    private bool _dragged;
    private readonly DispatcherTimer _pulse;
    private DateTime _pulseStart = DateTime.Now;
    private readonly Dictionary<TextShape, FormattedText> _textCache = new();

    // Highlight state
    private AGeometry? _highlight;
    private AGeometry? _highlightTracks;
    private List<(double Width, AGeometry Lines)> _highlightTrackGroups = new();
    private Pad? _hover;

    public BoardView()
    {
        ClipToBounds = true;
        Focusable = true;
        _pulse = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    public Side Side { get; private set; } = Side.Front;
    public ViewMode Mode { get; private set; } = ViewMode.Realistic;
    public bool ShowParts { get; set; } = true;

    /// <summary>KiCad's own renders of each side (with and without parts), drawn instead of the flat part bodies.</summary>
    public sealed record KiCadRender(Bitmap Image, Rect2 Area, bool Mirrored);
    private readonly Dictionary<(Side, bool), KiCadRender> _renders = new();

    public void SetRender(Side side, bool withParts, KiCadRender? render)
    {
        if (render is null) _renders.Remove((side, withParts)); else _renders[(side, withParts)] = render;
        InvalidateVisual();
    }

    public void ClearRenders() { _renders.Clear(); InvalidateVisual(); }

    /// <summary>True when the realistic view shows KiCad's render for the current side.</summary>
    public bool HasRender => _renders.ContainsKey((Side, ShowParts));
    public string? HighlightNet { get; private set; }
    public IReadOnlyCollection<string> HighlightRefs { get; private set; } = Array.Empty<string>();
    public Vec2? Marker { get; private set; }
    public Severity MarkerSeverity { get; private set; }

    /// <summary>When set, copper is coloured by the simulated voltage and LEDs glow.</summary>
    public Simulator? Simulation { get; set; }
    private Footprint? _heldSwitch;

    /// <summary>Oscilloscope probes to draw on the board.</summary>
    public IReadOnlyList<Probe> Probes { get; set; } = Array.Empty<Probe>();

    public event Action<Pad?>? PadClicked;
    public event Action<Footprint, bool>? SwitchHeld;
    /// <summary>A click on a part body where there is no pad.</summary>
    public event Action<Footprint>? BodyClicked;
    public event Action<Pad?>? PadHovered;
    public event Action<Vec2>? CursorMoved;

    public void SetScene(BoardScene? scene)
    {
        _scene = scene;
        _textCache.Clear();
        ClearHighlight();
        Fit();
    }

    public void SetSide(Side side) { Side = side; Fit(); }
    public void SetMode(ViewMode mode) { Mode = mode; InvalidateVisual(); }

    public void Fit()
    {
        if (_scene is null || Bounds.Width < 10) { InvalidateVisual(); return; }
        var b = _scene.Bounds;
        _zoom = Math.Min(Bounds.Width / (b.Width * 1.12), Bounds.Height / (b.Height * 1.12));
        var c = b.Center;
        _pan = new Vector(Bounds.Width / 2 - c.X * _zoom * MirrorSign, Bounds.Height / 2 - c.Y * _zoom);
        UpdateView();
    }

    private double MirrorSign => Side == Side.Back ? -1 : 1;

    private void UpdateView()
    {
        _view = Matrix.CreateScale(_zoom * MirrorSign, _zoom) * Matrix.CreateTranslation(_pan);
        InvalidateVisual();
    }

    public Vec2 ToBoard(Point p)
    {
        var inv = _view.Invert();
        var q = p.Transform(inv);
        return new Vec2(q.X, q.Y);
    }

    /// <summary>Zooms so that a point is centred, at a comfortable size.</summary>
    public void FocusOn(Vec2 p, double minZoomMm = 30)
    {
        var target = Math.Min(Bounds.Width, Bounds.Height) / minZoomMm;
        _zoom = Math.Max(_zoom, target);
        _pan = new Vector(Bounds.Width / 2 - p.X * _zoom * MirrorSign, Bounds.Height / 2 - p.Y * _zoom);
        UpdateView();
    }

    public void ClearHighlight()
    {
        HighlightNet = null;
        HighlightRefs = Array.Empty<string>();
        Marker = null;
        _highlight = null;
        _highlightTracks = null;
        _highlightTrackGroups.Clear();
        _pulse.Stop();
        InvalidateVisual();
    }

    /// <summary>Lights up every piece of copper on the given nets, plus the given pins and parts.</summary>
    public void Highlight(IEnumerable<string> nets, IEnumerable<string> refs, Vec2? marker = null, Severity severity = Severity.Info)
    {
        if (_scene is null) return;
        var netSet = nets.ToHashSet();
        var refSet = refs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HighlightNet = netSet.FirstOrDefault();
        HighlightRefs = refSet;
        Marker = marker;
        MarkerSeverity = severity;
        var b = _scene.Board;

        var polys = new List<IReadOnlyList<Vec2>>();
        foreach (var pad in b.Pads.Where(p => netSet.Contains(p.Net) || refSet.Contains(p.Id) || refSet.Contains(p.Footprint.Reference)))
            polys.AddRange(Outlines.Pad(pad));
        foreach (var v in b.Vias.Where(v => netSet.Contains(v.Net)))
            polys.Add(Outlines.Circle(v.Position, v.Size / 2, 20).ToList());
        foreach (var z in b.Zones.Where(z => netSet.Contains(z.Net) && !z.IsRuleArea))
            foreach (var f in z.Fills.Where(f => IsVisibleCopper(f.Layer)))
                polys.Add(f.Points);
        _highlight = BoardScene.Polygons(polys, FillRule.NonZero);
        _highlightTrackGroups = b.Tracks.Where(t => netSet.Contains(t.Net))
            .GroupBy(t => Math.Round(t.Width, 3))
            .Select(g => (g.Key, BoardScene.Lines(g.Select(t => t.IsArc ? Geometry2.ArcPolyline(t) : new[] { t.Start, t.End }))))
            .ToList();
        _pulseStart = DateTime.Now;
        if (marker is not null) _pulse.Start(); else _pulse.Stop();
        InvalidateVisual();
    }

    private bool IsVisibleCopper(string layer) => true;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Refit on the first layout, and when the panel below it appears or goes.
        if (e.PreviousSize.Width < 10 || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 40) Fit();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var factor = Math.Pow(1.18, e.Delta.Y);
        var newZoom = Math.Clamp(_zoom * factor, 0.5, 4000);
        factor = newZoom / _zoom;
        _pan = new Vector(p.X - (p.X - _pan.X) * factor, p.Y - (p.Y - _pan.Y) * factor);
        _zoom = newZoom;
        UpdateView();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        if (Simulation is not null && HitSwitch(ToBoard(e.GetPosition(this))) is { } sw)
        {
            _heldSwitch = sw;
            SwitchHeld?.Invoke(sw, true);
            e.Pointer.Capture(this);
            return;
        }
        _dragStart = e.GetPosition(this);
        _panStart = _pan;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragStart is { } start && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var d = p - start;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 3) _dragged = true;
            if (_dragged)
            {
                _pan = _panStart + d;
                UpdateView();
                return;
            }
        }
        var bp = ToBoard(p);
        CursorMoved?.Invoke(bp);
        var hit = HitPad(bp);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            PadHovered?.Invoke(hit);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        e.Pointer.Capture(null);
        if (_heldSwitch is { } held)
        {
            _heldSwitch = null;
            SwitchHeld?.Invoke(held, false);
            return;
        }
        if (_dragStart is not null && !_dragged)
        {
            var at = ToBoard(e.GetPosition(this));
            var pad = HitPad(at);
            if (pad is null && HitBody(at) is { } body) BodyClicked?.Invoke(body);
            else PadClicked?.Invoke(pad);
        }
        _dragStart = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F) { Fit(); e.Handled = true; }
    }

    public Footprint? HitBody(Vec2 p) =>
        _scene?.Sides[Side].Bodies.LastOrDefault(b => Core.Model.Geometry.PointInPolygon(p, b.Corners))?.Footprint;

    private Footprint? HitSwitch(Vec2 p)
    {
        if (_scene is null) return null;
        return _scene.Sides[Side].Bodies
            .Where(b => b.Kind == Core.Analysis.PartKind.Switch)
            .FirstOrDefault(b => Core.Model.Geometry.PointInPolygon(p, b.Corners))?.Footprint;
    }

    public Pad? HitPad(Vec2 p)
    {
        if (_scene is null) return null;
        var cu = Side == Side.Front ? "F.Cu" : "B.Cu";
        return _scene.Board.Pads
            .Where(pad => pad.Number.Length > 0 && (pad.OnLayer(cu) || pad.Kind == PadKind.ThroughHole) && pad.Bounds().Inflate(0.1).Contains(p))
            .FirstOrDefault(pad => pad.Contains(p, 0.05));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Backdrop, new Rect(Bounds.Size));
        DrawGrid(ctx);
        if (_scene is null) return;

        using (ctx.PushTransform(_view))
        {
            if (Mode == ViewMode.Realistic) DrawRealistic(ctx, _scene);
            else DrawLayers(ctx, _scene);
            if (Simulation is not null) DrawSimulation(ctx, _scene, Simulation);
            DrawHighlight(ctx);
            DrawHover(ctx);
        }
        DrawProbes(ctx);
        DrawMarker(ctx);
        DrawScaleBar(ctx);
        if (Simulation is not null) DrawLegend(ctx);
    }

    public static Color VoltageColor(NetResult r) => r.State switch
    {
        NetState.Floating => Color.Parse("#8a8f99"),
        NetState.Conflict => Color.Parse("#ff2d55"),
        NetState.Mid => Color.Parse("#b48cff"),
        NetState.Low => Color.Parse("#2f7bff"),
        _ when r.Volts >= 4.2 => Color.Parse("#ff5a36"),
        _ => Color.Parse("#ffb21e"),
    };

    private void DrawSimulation(DrawingContext ctx, BoardScene s, Simulator sim)
    {
        var blink = (DateTime.Now.Millisecond / 250) % 2 == 0;
        var any = false;
        foreach (var (net, r) in sim.Nets)
        {
            var g = s.NetOn(Side, net.Name);
            var color = VoltageColor(r);
            if (r.State == NetState.Conflict) { any = true; if (!blink) color = Color.Parse("#7a1426"); }
            // Rails keep the board readable: only their pads and tracks, faintly.
            var rail = net.IsRail;
            var alpha = rail ? 0.32 : r.State == NetState.Floating ? 0.55 : 0.9;
            var brush = new SolidColorBrush(color, alpha);
            if (!rail && g.Fill is not null) ctx.DrawGeometry(new SolidColorBrush(color, 0.35), null, g.Fill);
            foreach (var (w, lines) in g.Tracks)
            {
                if (!rail && r.State is NetState.High or NetState.Conflict)
                    ctx.DrawGeometry(null, new Pen(new SolidColorBrush(color, 0.25), w + 0.45, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), lines);
                ctx.DrawGeometry(null, new Pen(brush, w, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round,
                    dashStyle: r.State == NetState.Floating ? new DashStyle(new[] { 1.5, 1.5 }, 0) : null), lines);
            }
            if (g.Copper is not null) ctx.DrawGeometry(brush, null, g.Copper);
        }
        ctx.DrawGeometry(HoleBrush, null, s.Holes);

        // Bodies on top again, then light from the LEDs.
        if (ShowParts && Mode == ViewMode.Realistic && !HasRender)
            foreach (var body in s.Sides[Side].Bodies.Where(b => b.Kind != Core.Analysis.PartKind.Connector)) DrawBody(ctx, body);
        foreach (var body in s.Sides[Side].Bodies.Where(b => b.Kind == Core.Analysis.PartKind.Led))
        {
            var part = sim.Circuit.Part(body.Footprint.Reference);
            if (part is null || !sim.LedCurrent.TryGetValue(part, out var i) || i <= 0) continue;
            var (fill, _) = BodyColors(body);
            var c = ((SolidColorBrush)fill).Color;
            var center = new Vec2(body.Corners.Average(p => p.X), body.Corners.Average(p => p.Y));
            var strength = Math.Clamp(Math.Sqrt(i / 2e-3), 0.25, 1.6);
            var r = 1.2 + 2.6 * strength;
            var glow = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb((byte)(235 * Math.Min(1, strength)), 255, 255, 255), 0),
                    new GradientStop(Color.FromArgb((byte)(200 * Math.Min(1, strength)), c.R, c.G, c.B), 0.18),
                    new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                },
            };
            ctx.DrawEllipse(glow, null, BoardScene.P(center), r, r);
        }
        if (any) _pulse.Start();
    }

    private void DrawLegend(DrawingContext ctx)
    {
        var items = new (string Label, Color Color)[]
        {
            ("0 V", Color.Parse("#2f7bff")), ("3.3 V", Color.Parse("#ffb21e")), ("5 V", Color.Parse("#ff5a36")),
            ("between", Color.Parse("#b48cff")), ("floating", Color.Parse("#8a8f99")), ("conflict", Color.Parse("#ff2d55")),
        };
        var x = Bounds.Width - 18.0;
        var y = Bounds.Height - 48.0;
        var texts = items.Select(it => new FormattedText(it.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 12,
            new SolidColorBrush(Color.Parse("#c6ccd6")))).ToList();
        var width = texts.Sum(t => t.Width + 26) + 14;
        var box = new Rect(x - width, y, width, 30);
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#11151c"), 0.88), new Pen(new SolidColorBrush(Color.Parse("#2a313d")), 1), box, 10, 10);
        var cx = box.X + 14;
        for (var i = 0; i < items.Length; i++)
        {
            ctx.DrawEllipse(new SolidColorBrush(items[i].Color), null, new Point(cx + 5, y + 15), 5, 5);
            ctx.DrawText(texts[i], new Point(cx + 15, y + 15 - texts[i].Height / 2));
            cx += texts[i].Width + 26;
        }
    }

    private void DrawGrid(DrawingContext ctx)
    {
        var dot = new SolidColorBrush(Color.Parse("#ffffff"), 0.045);
        const double step = 28;
        for (var x = step / 2; x < Bounds.Width; x += step)
            for (var y = step / 2; y < Bounds.Height; y += step)
                ctx.FillRectangle(dot, new Rect(x, y, 1.4, 1.4));
    }

    private void DrawRealistic(DrawingContext ctx, BoardScene s)
    {
        var cu = Side == Side.Front ? "F.Cu" : "B.Cu";
        var sg = s.Sides[Side];

        if (_renders.TryGetValue((Side, ShowParts), out var render))
        {
            if (s.Substrate is not null)
                using (ctx.PushTransform(Matrix.CreateTranslation(0.6 * MirrorSign, 0.9)))
                    ctx.DrawGeometry(new SolidColorBrush(Colors.Black, 0.45), null, s.Substrate);
            var a = render.Area;
            var dest = new Rect(a.MinX, a.MinY, a.Width, a.Height);
            // A bottom render is seen from below; flip it back into board coordinates (the view mirrors it again).
            var flip = render.Mirrored
                ? Matrix.CreateTranslation(-dest.Center.X, 0) * Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(dest.Center.X, 0)
                : Matrix.Identity;
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            using (ctx.PushTransform(flip))
                ctx.DrawImage(render.Image, new Rect(render.Image.Size), dest);
            return;
        }

        // Drop shadow and FR4 edge.
        if (s.Substrate is not null)
        {
            using (ctx.PushTransform(Matrix.CreateTranslation(0.6 * MirrorSign, 0.9)))
                ctx.DrawGeometry(new SolidColorBrush(Colors.Black, 0.45), null, s.Substrate);
            ctx.DrawGeometry(Mask, EdgePen, s.Substrate);
        }

        if (s.Copper.TryGetValue(cu, out var layer))
        {
            if (layer.Fill is not null) ctx.DrawGeometry(CopperUnderMask, null, layer.Fill);
            foreach (var (w, g) in layer.Tracks)
                ctx.DrawGeometry(null, new Pen(CopperUnderMask, w, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
            if (layer.Vias is not null) ctx.DrawGeometry(CopperUnderMask, null, layer.Vias);
        }
        if (sg.CoveredPads is not null) ctx.DrawGeometry(CopperUnderMask, null, sg.CoveredPads);
        if (sg.ExposedPads is not null) ctx.DrawGeometry(Gold, null, sg.ExposedPads);
        ctx.DrawGeometry(HoleBrush, null, s.Holes);

        foreach (var (w, g) in sg.Silk)
            if (w == 0) ctx.DrawGeometry(Silk, null, g);
            else ctx.DrawGeometry(null, new Pen(Silk, w, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        foreach (var t in sg.SilkText) DrawText(ctx, t, Silk);

        if (ShowParts) foreach (var body in sg.Bodies) DrawBody(ctx, body);
    }

    private void DrawLayers(DrawingContext ctx, BoardScene s)
    {
        if (s.Substrate is not null)
            ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#1a1f27")), new Pen(new SolidColorBrush(Color.Parse("#d8c58f")), 0.12), s.Substrate);
        var order = s.Board.CopperLayers.Reverse().ToList();
        if (Side == Side.Back) order.Reverse();
        foreach (var name in order)
        {
            if (!s.Copper.TryGetValue(name, out var layer)) continue;
            var color = LayerColors.GetValueOrDefault(name, Color.Parse("#9aa4b2"));
            var top = name == (Side == Side.Front ? "F.Cu" : "B.Cu");
            var brush = new SolidColorBrush(color, top ? 0.85 : 0.33);
            if (layer.Fill is not null) ctx.DrawGeometry(new SolidColorBrush(color, top ? 0.16 : 0.07), null, layer.Fill);
            foreach (var (w, g) in layer.Tracks)
                ctx.DrawGeometry(null, new Pen(brush, w, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
            if (layer.Pads is not null) ctx.DrawGeometry(brush, null, layer.Pads);
            if (layer.Vias is not null) ctx.DrawGeometry(new SolidColorBrush(Color.Parse("#c9ccd1"), 0.8), null, layer.Vias);
        }
        ctx.DrawGeometry(HoleBrush, null, s.Holes);
        var sg = s.Sides[Side];
        var silk = new SolidColorBrush(Color.Parse("#e8e8e8"), 0.7);
        foreach (var (w, g) in sg.Silk)
            if (w == 0) ctx.DrawGeometry(silk, null, g);
            else ctx.DrawGeometry(null, new Pen(silk, w, lineCap: PenLineCap.Round), g);
        foreach (var t in sg.SilkText) DrawText(ctx, t, silk);
    }

    private void DrawBody(DrawingContext ctx, BoardScene.Body body)
    {
        var (fill, edge) = BodyColors(body);
        ctx.DrawGeometry(fill, new Pen(edge, 0.06), body.Shape);

        // Pin 1 mark for chips.
        if (body.Kind is PartKind.Ic or PartKind.Regulator && body.Footprint.PadsNumbered("1").FirstOrDefault() is { } pin1)
        {
            var c = body.Corners;
            var nearest = c.OrderBy(p => p.DistanceTo(pin1.Position)).First();
            var center = new Vec2(c.Average(p => p.X), c.Average(p => p.Y));
            var dot = nearest + (center - nearest) * 0.18;
            var r = Math.Min(c[0].DistanceTo(c[1]), c[1].DistanceTo(c[2])) * 0.06;
            ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#6b6f78")), null, BoardScene.P(dot), r, r);
        }

        // Crystals show their frequency; amber when it is not what the board says.
        if (body.Kind == PartKind.Crystal && Simulation?.Circuit.Part(body.Footprint.Reference) is { } xtal)
        {
            var hz = Simulation.CrystalHz(xtal);
            var changed = hz is { } hzNow && xtal.Value is { } hzBoard && Math.Abs(hzNow / hzBoard - 1) > 1e-6;
            var mid0 = new Vec2(body.Corners.Average(p => p.X), body.Corners.Average(p => p.Y));
            var label0 = new FormattedText(hz is { } f ? $"{f / 1e6:0.###} MHz" : "?", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 0.75,
                new SolidColorBrush(Color.Parse(changed ? "#ffb21e" : "#1d2028")));
            using (ctx.PushTransform(Matrix.CreateTranslation(mid0.X, mid0.Y)))
            using (ctx.PushTransform(Side == Side.Back ? Matrix.CreateScale(-1, 1) : Matrix.Identity))
                ctx.DrawText(label0, new Point(-label0.Width / 2, -label0.Height / 2));
            if (changed) ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.Parse("#ffb21e")), 0.12), body.Shape);
            return;
        }

        // Reference on bigger bodies.
        var w = body.Corners[0].DistanceTo(body.Corners[1]);
        var h = body.Corners[1].DistanceTo(body.Corners[2]);
        var size = Math.Min(Math.Min(w, h) * 0.38, 1.4);
        if (size * _zoom < 7) return;
        var label = new FormattedText(body.Footprint.Reference, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, size,
            new SolidColorBrush(Color.Parse(body.Kind is PartKind.Connector ? "#4a4a4a" : "#c9ccd3"), 0.85));
        var mid = new Vec2(body.Corners.Average(p => p.X), body.Corners.Average(p => p.Y));
        using (ctx.PushTransform(Matrix.CreateTranslation(mid.X, mid.Y)))
        using (ctx.PushTransform(Side == Side.Back ? Matrix.CreateScale(-1, 1) : Matrix.Identity))
            ctx.DrawText(label, new Point(-label.Width / 2, -label.Height / 2));
    }

    private static (IBrush Fill, IBrush Edge) BodyColors(BoardScene.Body body)
    {
        var value = body.Footprint.Value.ToUpperInvariant();
        string fill = body.Kind switch
        {
            PartKind.Ic or PartKind.Regulator or PartKind.Tvs or PartKind.Transistor => "#1d1f24",
            PartKind.Resistor => "#202226",
            PartKind.Capacitor => "#b58a5a",
            PartKind.Inductor => "#3a3d44",
            PartKind.Diode => "#26282d",
            PartKind.Led when value.Contains("RED") => "#ff4b4b",
            PartKind.Led when value.Contains("GREEN") => "#3ee07a",
            PartKind.Led when value.Contains("BLUE") => "#4b8bff",
            PartKind.Led when value.Contains("YELLOW") => "#ffd84b",
            PartKind.Led => "#f4f4f4",
            PartKind.Crystal => "#c7cbd2",
            PartKind.Switch => "#2b2e34",
            PartKind.Antenna => "#ece7d8",
            PartKind.Connector when body.Footprint.LibId.Contains("USB") => "#b9bec7",
            PartKind.Connector when body.Footprint.LibId.Contains("TerminalBlock") => "#2f8a4a",
            PartKind.Connector when body.Footprint.LibId.Contains("JST") => "#efe9d6",
            PartKind.Connector => "#17181b",
            _ => "#2a2c31",
        };
        var c = Color.Parse(fill);
        var alpha = body.Kind == PartKind.Led ? 0.92 : 0.96;
        return (new SolidColorBrush(c, alpha), new SolidColorBrush(Color.FromRgb((byte)(c.R * 0.6), (byte)(c.G * 0.6), (byte)(c.B * 0.6))));
    }

    private void DrawText(DrawingContext ctx, TextShape t, IBrush brush)
    {
        if (t.Height * _zoom < 3.5) return;
        if (!_textCache.TryGetValue(t, out var ft))
        {
            ft = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, SilkFont, t.Height * 1.32, brush);
            _textCache[t] = ft;
        }
        // Keep text upright, as KiCad does.
        var angle = t.Angle % 360;
        if (angle > 90 && angle <= 270) angle -= 180;
        if (angle < -90) angle += 180;
        // Multi-line text: KiCad anchors the first line; Avalonia lays out the block.
        var lines = t.Text.Count(ch => ch == '\n') + 1;
        var lineH = ft.Height / lines;
        var dx = t.Align switch { TextAlign.Left => 0, TextAlign.Right => -ft.Width, _ => -ft.Width / 2 };
        var dy = t.Top ? -lineH * 0.2 : t.Bottom ? -lineH * 0.85 : lines > 1 ? -lineH / 2 : -ft.Height / 2;
        var m = Matrix.CreateTranslation(dx, dy)
                * (t.Mirrored ? Matrix.CreateScale(-1, 1) : Matrix.Identity)
                * Matrix.CreateRotation(-angle * Math.PI / 180)
                * Matrix.CreateTranslation(t.Position.X, t.Position.Y);
        using (ctx.PushTransform(m)) ctx.DrawText(ft, new Point(0, 0));
    }

    private void DrawHighlight(DrawingContext ctx)
    {
        if (_highlight is null && _highlightTrackGroups.Count == 0) return;
        if (_scene?.Substrate is not null) ctx.DrawGeometry(Dim, null, _scene.Substrate);
        var glow = new SolidColorBrush(Accent, 0.28);
        var solid = new SolidColorBrush(Accent, 0.95);
        foreach (var (w, g) in _highlightTrackGroups)
        {
            ctx.DrawGeometry(null, new Pen(glow, w + 0.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
            ctx.DrawGeometry(null, new Pen(solid, w, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), g);
        }
        if (_highlight is not null)
        {
            ctx.DrawGeometry(glow, new Pen(glow, 0.5), _highlight);
            ctx.DrawGeometry(solid, null, _highlight);
        }
    }

    private void DrawHover(DrawingContext ctx)
    {
        if (_hover is null) return;
        var pen = new Pen(Brushes.White, Math.Max(0.06, 1.6 / _zoom));
        foreach (var poly in Outlines.Pad(_hover))
            ctx.DrawGeometry(new SolidColorBrush(Colors.White, 0.25), pen, BoardScene.Polygons(new[] { poly }, FillRule.NonZero));
    }

    private void DrawProbes(DrawingContext ctx)
    {
        foreach (var probe in Probes)
        {
            var p = new Point(probe.Pad.Position.X, probe.Pad.Position.Y).Transform(_view);
            var brush = new SolidColorBrush(probe.Color);
            // A probe tip with a hook, and the channel number on a tag.
            ctx.DrawEllipse(new SolidColorBrush(probe.Color, 0.25), new Pen(brush, 2), p, 9, 9);
            ctx.DrawEllipse(brush, null, p, 3, 3);
            var tag = new Point(p.X + 14, p.Y - 26);
            ctx.DrawLine(new Pen(brush, 2), new Point(p.X + 6, p.Y - 6), tag + new Vector(0, 10));
            ctx.DrawRectangle(brush, null, new Rect(tag.X - 2, tag.Y - 1, 20, 18), 4, 4);
            var t = new FormattedText(probe.Channel.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 12, new SolidColorBrush(Color.Parse("#05080b")));
            ctx.DrawText(t, new Point(tag.X + 8 - t.Width / 2, tag.Y + 8 - t.Height / 2));
        }
    }

    private void DrawMarker(DrawingContext ctx)
    {
        if (Marker is not { } m) return;
        var p = new Point(m.X, m.Y).Transform(_view);
        var t = (DateTime.Now - _pulseStart).TotalSeconds;
        var color = MarkerSeverity switch
        {
            Severity.Error => Color.Parse("#ff5c5c"),
            Severity.Warning => Color.Parse("#ffb547"),
            _ => Color.Parse("#6ca0ff"),
        };
        for (var i = 0; i < 2; i++)
        {
            var phase = (t * 0.9 + i * 0.5) % 1.0;
            var r = 10 + phase * 34;
            ctx.DrawEllipse(null, new Pen(new SolidColorBrush(color, 1 - phase), 2.5), p, r, r);
        }
        ctx.DrawEllipse(new SolidColorBrush(color), new Pen(Brushes.White, 1.5), p, 4.5, 4.5);
    }

    private void DrawScaleBar(DrawingContext ctx)
    {
        if (_scene is null) return;
        var mm = new[] { 0.5, 1, 2, 5, 10, 20, 50 }.FirstOrDefault(v => v * _zoom > 70, 50);
        var len = mm * _zoom;
        var x = 18;
        var y = Bounds.Height - 22;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#c6ccd6")), 1.5);
        ctx.DrawLine(pen, new Point(x, y), new Point(x + len, y));
        ctx.DrawLine(pen, new Point(x, y - 4), new Point(x, y + 4));
        ctx.DrawLine(pen, new Point(x + len, y - 4), new Point(x + len, y + 4));
        var ft = new FormattedText($"{mm:0.#} mm", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFont, 11, pen.Brush);
        ctx.DrawText(ft, new Point(x + len + 8, y - ft.Height / 2));
    }
}
