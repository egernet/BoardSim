namespace BoardSim.Core.Model;

public static class Geometry
{
    public static bool PointInPolygon(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b) => p.DistanceTo(ClosestPointOnSegment(p, a, b));

    public static Vec2 ClosestPointOnSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        if (len2 == 0) return a;
        var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return a + ab * t;
    }

    public static double DistanceToPolygonEdge(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        var best = double.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            best = Math.Min(best, DistanceToSegment(p, poly[j], poly[i]));
        return best;
    }

    public static Rect2 BoundsOf(IEnumerable<Vec2> points)
    {
        var r = Rect2.Empty;
        foreach (var p in points) r = r.Include(p);
        return r;
    }

    /// <summary>Circle through three points; null when they are on a line.</summary>
    public static (Vec2 Center, double Radius)? Circumcircle(Vec2 a, Vec2 b, Vec2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12) return null;
        var a2 = a.X * a.X + a.Y * a.Y;
        var b2 = b.X * b.X + b.Y * b.Y;
        var c2 = c.X * c.X + c.Y * c.Y;
        var ux = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
        var uy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
        var center = new Vec2(ux, uy);
        return (center, center.DistanceTo(a));
    }

    /// <summary>Splits an arc given by start, mid and end into a polyline.</summary>
    public static IReadOnlyList<Vec2> ArcPoints(Vec2 start, Vec2 mid, Vec2 end, int segments = 24)
    {
        var circle = Circumcircle(start, mid, end);
        if (circle is null) return new[] { start, end };
        var (c, r) = circle.Value;
        double A(Vec2 p) => Math.Atan2(p.Y - c.Y, p.X - c.X);
        var a0 = A(start);
        var am = A(mid);
        var a1 = A(end);
        // Pick the sweep direction that passes through the mid point.
        var sweep = Normalize(a1 - a0);
        var toMid = Normalize(am - a0);
        if (!(sweep > 0 ? toMid > 0 && toMid < sweep : toMid < 0 && toMid > sweep))
            sweep = sweep > 0 ? sweep - 2 * Math.PI : sweep + 2 * Math.PI;
        var pts = new Vec2[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var a = a0 + sweep * i / segments;
            pts[i] = new Vec2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        return pts;
    }

    private static double Normalize(double a)
    {
        while (a <= -Math.PI) a += 2 * Math.PI;
        while (a > Math.PI) a -= 2 * Math.PI;
        return a;
    }

    /// <summary>The points that outline a shape, used for bounds.</summary>
    public static IEnumerable<Vec2> Points(Shape s) => s switch
    {
        LineShape l => new[] { l.Start, l.End },
        ArcShape a => ArcPoints(a.Start, a.Mid, a.End, 8),
        CircleShape c => new[] { c.Center + new Vec2(-c.Radius, -c.Radius), c.Center + new Vec2(c.Radius, c.Radius) },
        PolyShape p => p.Points,
        TextShape t => new[] { t.Position },
        _ => Array.Empty<Vec2>(),
    };
}
