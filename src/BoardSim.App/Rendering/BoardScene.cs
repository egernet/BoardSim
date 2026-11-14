using Avalonia;
using Avalonia.Media;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;
using AGeometry = Avalonia.Media.Geometry;

namespace BoardSim.App.Rendering;

/// <summary>
/// Pre-built geometry for drawing one board. Building happens once per board
/// and side; drawing then only replays the geometries.
/// </summary>
public sealed class BoardScene
{
    public Board Board { get; }
    public Circuit? Circuit { get; }
    public Rect2 Bounds { get; }

    public AGeometry? Substrate { get; }
    public IReadOnlyList<Vec2[]> OutlineLoops { get; }

    /// <summary>Per copper layer: zone fills, tracks grouped by width, via annuli.</summary>
    public Dictionary<string, LayerGeometry> Copper { get; } = new();
    public Dictionary<Side, SideGeometry> Sides { get; } = new();
    public AGeometry Holes { get; }

    public sealed class LayerGeometry
    {
        public AGeometry? Fill { get; init; }
        public List<(double Width, AGeometry Lines)> Tracks { get; } = new();
        public AGeometry? Pads { get; init; }
        public AGeometry? Vias { get; init; }
    }

    public sealed class SideGeometry
    {
        public AGeometry? ExposedPads { get; init; }
        public AGeometry? CoveredPads { get; init; }
        public List<(double Width, AGeometry Lines)> Silk { get; } = new();
        public List<TextShape> SilkText { get; } = new();
        public List<Body> Bodies { get; } = new();
    }

    public sealed record Body(Footprint Footprint, PartKind Kind, AGeometry Shape, Vec2[] Corners);

    public BoardScene(Board board, Circuit? circuit)
    {
        Board = board;
        Circuit = circuit;
        Bounds = board.Bounds();

        var loops = Outlines.BoardLoops(board);
        OutlineLoops = loops.Select(l => l.ToArray()).ToList();
        if (loops.Count > 0) Substrate = Polygons(loops, FillRule.EvenOdd);

        foreach (var layer in board.CopperLayers)
        {
            var fills = board.Zones.Where(z => !z.IsRuleArea).SelectMany(z => z.Fills).Where(f => f.Layer == layer).Select(f => f.Points).ToList();
            var lg = new LayerGeometry
            {
                Fill = fills.Count > 0 ? Polygons(fills, FillRule.NonZero) : null,
                Pads = Polygons(board.Pads.Where(p => p.OnLayer(layer)).SelectMany(Outlines.Pad).ToList(), FillRule.NonZero),
                Vias = Polygons(board.Vias.Where(v => v.Layers.Contains(layer)).Select(v => Outlines.Circle(v.Position, v.Size / 2, 20).ToList()).ToList(), FillRule.NonZero),
            };
            foreach (var group in board.Tracks.Where(t => t.Layer == layer).GroupBy(t => Math.Round(t.Width, 3)))
                lg.Tracks.Add((group.Key, Lines(group.Select(t => t.IsArc ? Geometry2.ArcPolyline(t) : new[] { t.Start, t.End }))));
            Copper[layer] = lg;
        }

        var holes = new List<IReadOnlyList<Vec2>>();
        foreach (var p in board.Pads.Where(p => p.Drill.X > 0))
            holes.Add(p.Drill.X == p.Drill.Y
                ? Outlines.Circle(p.Position, p.Drill.X / 2, 20).ToList()
                : OvalHole(p));
        foreach (var v in board.Vias) holes.Add(Outlines.Circle(v.Position, v.Drill / 2, 12).ToList());
        Holes = Polygons(holes, FillRule.NonZero);

        foreach (var side in new[] { Side.Front, Side.Back })
        {
            var cu = side == Side.Front ? "F.Cu" : "B.Cu";
            var mask = side == Side.Front ? "F.Mask" : "B.Mask";
            var silk = side == Side.Front ? "F.SilkS" : "B.SilkS";
            var pads = board.Pads.Where(p => p.OnLayer(cu) || p.OnLayer(mask)).ToList();
            var sg = new SideGeometry
            {
                ExposedPads = Polygons(pads.Where(p => p.OnLayer(mask)).SelectMany(Outlines.Pad).ToList(), FillRule.NonZero),
                CoveredPads = Polygons(pads.Where(p => !p.OnLayer(mask) && p.OnLayer(cu)).SelectMany(Outlines.Pad).ToList(), FillRule.NonZero),
            };
            var silkShapes = board.Graphics.Concat(board.Footprints.SelectMany(f => f.Graphics))
                .Where(g => g.Layer == silk || g.Layer == silk.Replace("SilkS", "Silkscreen")).ToList();
            foreach (var group in silkShapes.Where(s => s is not TextShape).GroupBy(s => Math.Round(Math.Max(s.Width, 0.08), 3)))
            {
                var lines = new List<IReadOnlyList<Vec2>>();
                var filled = new List<IReadOnlyList<Vec2>>();
                foreach (var s in group)
                {
                    switch (s)
                    {
                        case LineShape l: lines.Add(new[] { l.Start, l.End }); break;
                        case ArcShape a: lines.Add(Core.Model.Geometry.ArcPoints(a.Start, a.Mid, a.End, 16)); break;
                        case CircleShape c when c.Filled: filled.Add(Outlines.Circle(c.Center, c.Radius, 24).ToList()); break;
                        case CircleShape c: lines.Add(Outlines.Circle(c.Center, c.Radius, 32).Append(c.Center + new Vec2(c.Radius, 0)).ToList()); break;
                        case PolyShape p when p.Filled: filled.Add(p.Points); break;
                        case PolyShape p: lines.Add(p.Points.Append(p.Points[0]).ToList()); break;
                    }
                }
                if (lines.Count > 0) sg.Silk.Add((group.Key, Lines(lines)));
                if (filled.Count > 0) sg.Silk.Add((0, Polygons(filled, FillRule.NonZero)));
            }
            sg.SilkText.AddRange(silkShapes.OfType<TextShape>());

            foreach (var fp in board.Footprints.Where(f => f.Side == side && !f.DoNotPopulate))
            {
                var kind = circuit?.Part(fp.Reference)?.Kind ?? PartKind.Other;
                if (kind is PartKind.TestPoint or PartKind.MountingHole) continue;
                if (Outlines.Body(fp) is { } body)
                    sg.Bodies.Add(new Body(fp, kind, Polygons(new[] { body }, FillRule.NonZero), body.ToArray()));
            }
            Sides[side] = sg;
        }
    }

    public sealed class NetGeometry
    {
        public AGeometry? Copper { get; init; }
        public AGeometry? Fill { get; init; }
        public List<(double Width, AGeometry Lines)> Tracks { get; } = new();
    }

    private readonly Dictionary<(Side, string), NetGeometry> _netCache = new();

    /// <summary>The copper of one net as seen from one side: outer-layer tracks, fills, its pads and vias.</summary>
    public NetGeometry NetOn(Side side, string net)
    {
        if (_netCache.TryGetValue((side, net), out var cached)) return cached;
        var cu = side == Side.Front ? "F.Cu" : "B.Cu";
        var polys = new List<IReadOnlyList<Vec2>>();
        foreach (var pad in Board.Pads.Where(p => p.Net == net && (p.OnLayer(cu) || p.Kind == PadKind.ThroughHole)))
            polys.AddRange(Outlines.Pad(pad));
        foreach (var v in Board.Vias.Where(v => v.Net == net))
            polys.Add(Outlines.Circle(v.Position, v.Size / 2, 16).ToList());
        var fills = Board.Zones.Where(z => z.Net == net && !z.IsRuleArea).SelectMany(z => z.Fills).Where(f => f.Layer == cu).Select(f => f.Points).ToList();
        var g = new NetGeometry
        {
            Copper = polys.Count > 0 ? Polygons(polys, FillRule.NonZero) : null,
            Fill = fills.Count > 0 ? Polygons(fills, FillRule.NonZero) : null,
        };
        foreach (var group in Board.Tracks.Where(t => t.Net == net && t.Layer == cu).GroupBy(t => Math.Round(t.Width, 3)))
            g.Tracks.Add((group.Key, Lines(group.Select(t => t.IsArc ? Geometry2.ArcPolyline(t) : new[] { t.Start, t.End }))));
        _netCache[(side, net)] = g;
        return g;
    }

    private static List<Vec2> OvalHole(Pad p)
    {
        var hx = p.Drill.X / 2;
        var hy = p.Drill.Y / 2;
        var r = Math.Min(hx, hy);
        var pts = new List<Vec2>();
        for (var i = 0; i < 24; i++)
        {
            var a = 2 * Math.PI * i / 24;
            var x = Math.Cos(a) * r + Math.Sign(Math.Cos(a)) * (hx - r);
            var y = Math.Sin(a) * r + Math.Sign(Math.Sin(a)) * (hy - r);
            pts.Add(p.Position + new Vec2(x, y).Rotate(p.Angle));
        }
        return pts;
    }

    public static Point P(Vec2 v) => new(v.X, v.Y);

    public static AGeometry Polygons(IEnumerable<IReadOnlyList<Vec2>> polys, FillRule rule)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        ctx.SetFillRule(rule);
        foreach (var poly in polys)
        {
            if (poly.Count < 3) continue;
            ctx.BeginFigure(P(poly[0]), true);
            for (var i = 1; i < poly.Count; i++) ctx.LineTo(P(poly[i]));
            ctx.EndFigure(true);
        }
        return g;
    }

    public static AGeometry Lines(IEnumerable<IReadOnlyList<Vec2>> lines)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        foreach (var line in lines)
        {
            if (line.Count < 2) continue;
            ctx.BeginFigure(P(line[0]), false);
            for (var i = 1; i < line.Count; i++) ctx.LineTo(P(line[i]));
            ctx.EndFigure(false);
        }
        return g;
    }
}

internal static class Geometry2
{
    public static IReadOnlyList<Vec2> ArcPolyline(Track t) =>
        Core.Model.Geometry.ArcPoints(t.Start, t.Mid!.Value, t.End, 16);
}
