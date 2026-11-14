namespace BoardSim.Core.Model;

/// <summary>Polygons for drawing: pad shapes, the board outline and part bodies.</summary>
public static class Outlines
{
    /// <summary>The copper of a pad as one or more polygons.</summary>
    public static IReadOnlyList<IReadOnlyList<Vec2>> Pad(Pad pad)
    {
        var result = new List<IReadOnlyList<Vec2>>();
        var hx = pad.Size.X / 2;
        var hy = pad.Size.Y / 2;
        IEnumerable<Vec2> local = pad.Shape switch
        {
            PadShape.Circle => Circle(new Vec2(0, 0), hx, 28),
            PadShape.Oval => Stadium(hx, hy),
            PadShape.RoundRect => RoundRect(hx, hy, Math.Min(hx, hy) * 2 * pad.RoundRectRatio),
            _ => new[] { new Vec2(-hx, -hy), new Vec2(hx, -hy), new Vec2(hx, hy), new Vec2(-hx, hy) },
        };
        if (pad.Size.X > 0 && pad.Size.Y > 0)
            result.Add(local.Select(p => pad.Position + p.Rotate(pad.Angle)).ToList());
        result.AddRange(pad.Primitives);
        return result;
    }

    public static IEnumerable<Vec2> Circle(Vec2 c, double r, int n = 32)
    {
        for (var i = 0; i < n; i++)
        {
            var a = 2 * Math.PI * i / n;
            yield return new Vec2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
    }

    private static IEnumerable<Vec2> Stadium(double hx, double hy)
    {
        var r = Math.Min(hx, hy);
        return RoundRect(hx, hy, r);
    }

    private static IEnumerable<Vec2> RoundRect(double hx, double hy, double r)
    {
        r = Math.Clamp(r, 0, Math.Min(hx, hy));
        if (r < 1e-6)
        {
            yield return new Vec2(-hx, -hy); yield return new Vec2(hx, -hy);
            yield return new Vec2(hx, hy); yield return new Vec2(-hx, hy);
            yield break;
        }
        var corners = new[] { new Vec2(hx - r, -hy + r), new Vec2(hx - r, hy - r), new Vec2(-hx + r, hy - r), new Vec2(-hx + r, -hy + r) };
        var start = new[] { -90.0, 0, 90, 180 };
        for (var c = 0; c < 4; c++)
            for (var i = 0; i <= 6; i++)
            {
                var a = (start[c] + 90.0 * i / 6) * Math.PI / 180;
                yield return new Vec2(corners[c].X + r * Math.Cos(a), corners[c].Y + r * Math.Sin(a));
            }
    }

    /// <summary>
    /// Closed loops of the Edge.Cuts layer. The largest is the board outline,
    /// the rest are cut-outs.
    /// </summary>
    public static List<List<Vec2>> BoardLoops(Board board)
    {
        var loops = new List<List<Vec2>>();
        var open = new List<List<Vec2>>();
        foreach (var s in board.Outline)
        {
            switch (s)
            {
                case LineShape l: open.Add(new List<Vec2> { l.Start, l.End }); break;
                case ArcShape a: open.Add(Geometry.ArcPoints(a.Start, a.Mid, a.End, 16).ToList()); break;
                case CircleShape c: loops.Add(Circle(c.Center, c.Radius, 48).ToList()); break;
                case PolyShape p when p.Points.Count > 2: loops.Add(p.Points.ToList()); break;
            }
        }

        // Chain the open pieces end to end.
        const double tol = 0.02;
        while (open.Count > 0)
        {
            var chain = open[0];
            open.RemoveAt(0);
            var grew = true;
            while (grew && chain[0].DistanceTo(chain[^1]) > tol)
            {
                grew = false;
                for (var i = 0; i < open.Count; i++)
                {
                    var piece = open[i];
                    if (piece[0].DistanceTo(chain[^1]) <= tol) chain.AddRange(piece.Skip(1));
                    else if (piece[^1].DistanceTo(chain[^1]) <= tol) chain.AddRange(Enumerable.Reverse(piece).Skip(1));
                    else if (piece[^1].DistanceTo(chain[0]) <= tol) chain.InsertRange(0, piece.Take(piece.Count - 1));
                    else if (piece[0].DistanceTo(chain[0]) <= tol) chain.InsertRange(0, Enumerable.Reverse(piece).Take(piece.Count - 1));
                    else continue;
                    open.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
            if (chain.Count > 2) loops.Add(chain);
        }
        return loops.OrderByDescending(l => Math.Abs(Area(l))).ToList();
    }

    public static double Area(IReadOnlyList<Vec2> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            a += (poly[j].X + poly[i].X) * (poly[j].Y - poly[i].Y);
        return a / 2;
    }

    /// <summary>
    /// The part's body as a rotated rectangle, from its fabrication drawing
    /// (or its courtyard when there is none). Null when neither exists.
    /// </summary>
    public static IReadOnlyList<Vec2>? Body(Footprint fp)
    {
        var side = fp.Side == Side.Front ? "F." : "B.";
        var shapes = fp.Graphics.Where(g => g.Layer == side + "Fab" && g is not TextShape).ToList();
        var shrink = 0.0;
        if (shapes.Count == 0)
        {
            shapes = fp.Graphics.Where(g => g.Layer == side + "CrtYd").ToList();
            shrink = 0.25;
        }
        if (shapes.Count == 0) return null;
        var local = shapes.SelectMany(Geometry.Points).Select(p => (p - fp.Position).Rotate(-fp.Angle)).ToList();
        var b = Geometry.BoundsOf(local);
        if (b.IsEmpty || b.Width < 0.2 || b.Height < 0.2) return null;
        b = b.Inflate(-shrink);
        return new[] { new Vec2(b.MinX, b.MinY), new Vec2(b.MaxX, b.MinY), new Vec2(b.MaxX, b.MaxY), new Vec2(b.MinX, b.MaxY) }
            .Select(p => fp.Position + p.Rotate(fp.Angle)).ToList();
    }
}
