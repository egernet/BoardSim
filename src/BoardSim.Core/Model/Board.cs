namespace BoardSim.Core.Model;

/// <summary>A point in board coordinates: millimetres, y pointing down (as in KiCad).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Vec2 o) => (this - o).Length;

    /// <summary>Rotates the way KiCad does: positive angles turn counter-clockwise on screen.</summary>
    public Vec2 Rotate(double degrees)
    {
        if (degrees == 0) return this;
        var r = degrees * Math.PI / 180;
        var c = Math.Cos(r);
        var s = Math.Sin(r);
        return new(X * c + Y * s, -X * s + Y * c);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

public readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Rect2 Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
    public bool IsEmpty => MinX > MaxX;
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public Vec2 Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);

    public Rect2 Include(Vec2 p) => new(Math.Min(MinX, p.X), Math.Min(MinY, p.Y), Math.Max(MaxX, p.X), Math.Max(MaxY, p.Y));
    public Rect2 Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);
    public bool Contains(Vec2 p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;
    public bool Intersects(Rect2 o) => !(o.MinX > MaxX || o.MaxX < MinX || o.MinY > MaxY || o.MaxY < MinY);
}

public enum Side { Front, Back }

/// <summary>A drawn shape on a non-copper or copper layer (outline, silkscreen, fab...).</summary>
public abstract record Shape(string Layer, double Width);
public sealed record LineShape(string Layer, double Width, Vec2 Start, Vec2 End) : Shape(Layer, Width);
public sealed record ArcShape(string Layer, double Width, Vec2 Start, Vec2 Mid, Vec2 End) : Shape(Layer, Width);
public sealed record CircleShape(string Layer, double Width, Vec2 Center, double Radius, bool Filled) : Shape(Layer, Width);
public sealed record PolyShape(string Layer, double Width, IReadOnlyList<Vec2> Points, bool Filled) : Shape(Layer, Width);
public sealed record TextShape(string Layer, double Width, string Text, Vec2 Position, double Angle, double Height, bool Mirrored)
    : Shape(Layer, Width);

public enum PadKind { Smd, ThroughHole, NpThroughHole, Connect }
public enum PadShape { Rect, RoundRect, Circle, Oval, Trapezoid, Custom }

public sealed class Pad
{
    public required Footprint Footprint { get; init; }
    public required string Number { get; init; }
    public PadKind Kind { get; init; }
    public PadShape Shape { get; init; }
    /// <summary>Absolute position of the pad centre.</summary>
    public Vec2 Position { get; init; }
    public Vec2 Size { get; init; }
    /// <summary>Absolute rotation in degrees (footprint rotation included).</summary>
    public double Angle { get; init; }
    public double RoundRectRatio { get; init; }
    public Vec2 Drill { get; init; }
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    public string Net { get; set; } = "";
    public string PinFunction { get; set; } = "";
    /// <summary>Extra copper of custom pads, as absolute polygons.</summary>
    public IReadOnlyList<IReadOnlyList<Vec2>> Primitives { get; init; } = Array.Empty<IReadOnlyList<Vec2>>();

    public string Id => $"{Footprint.Reference}.{Number}";
    public bool HasNet => Net.Length > 0;
    public bool OnLayer(string layer) => Layers.Contains(layer);

    /// <summary>Corners of the pad's bounding rectangle, rotated.</summary>
    public IReadOnlyList<Vec2> Corners()
    {
        var hx = Size.X / 2;
        var hy = Size.Y / 2;
        return new[] { new Vec2(-hx, -hy), new Vec2(hx, -hy), new Vec2(hx, hy), new Vec2(-hx, hy) }
            .Select(p => Position + p.Rotate(Angle)).ToArray();
    }

    public Rect2 Bounds()
    {
        var r = Rect2.Empty;
        foreach (var c in Corners()) r = r.Include(c);
        foreach (var poly in Primitives) foreach (var p in poly) r = r.Include(p);
        return r;
    }

    /// <summary>True when <paramref name="p"/> lies on the pad's copper, with a tolerance.</summary>
    public bool Contains(Vec2 p, double tolerance = 0)
    {
        var local = (p - Position).Rotate(-Angle);
        var hx = Size.X / 2 + tolerance;
        var hy = Size.Y / 2 + tolerance;
        bool inside = Shape switch
        {
            PadShape.Circle => local.Length <= Size.X / 2 + tolerance,
            PadShape.Oval => InStadium(local, hx, hy),
            _ => Math.Abs(local.X) <= hx && Math.Abs(local.Y) <= hy,
        };
        if (inside) return true;
        foreach (var poly in Primitives)
            if (Geometry.PointInPolygon(p, poly) || Geometry.DistanceToPolygonEdge(p, poly) <= tolerance) return true;
        return false;
    }

    private static bool InStadium(Vec2 p, double hx, double hy)
    {
        if (hx > hy)
        {
            var cx = Math.Clamp(p.X, -(hx - hy), hx - hy);
            return new Vec2(p.X - cx, p.Y).Length <= hy;
        }
        var cy = Math.Clamp(p.Y, -(hy - hx), hy - hx);
        return new Vec2(p.X, p.Y - cy).Length <= hx;
    }

    public override string ToString() => $"{Id} [{Net}]";
}

public sealed class Footprint
{
    public required string Reference { get; init; }
    public string Value { get; init; } = "";
    public string LibId { get; init; } = "";
    public string Description { get; init; } = "";
    public Vec2 Position { get; init; }
    public double Angle { get; init; }
    public Side Side { get; init; }
    public bool IsSmd { get; init; }
    public bool DoNotPopulate { get; init; }
    public bool ExcludeFromBom { get; init; }
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Pad> Pads { get; } = new();
    public List<Shape> Graphics { get; } = new();

    /// <summary>Reference designator prefix, e.g. "R" for R14, "TP" for TP26.</summary>
    public string Prefix => new(Reference.TakeWhile(char.IsLetter).ToArray());

    public IEnumerable<Pad> PadsNumbered(string number) => Pads.Where(p => p.Number == number);

    public Rect2 Bounds()
    {
        var r = Rect2.Empty;
        foreach (var p in Pads) { var b = p.Bounds(); r = r.Include(new(b.MinX, b.MinY)).Include(new(b.MaxX, b.MaxY)); }
        foreach (var g in Graphics.Where(g => g.Layer.EndsWith("CrtYd")))
            foreach (var pt in Geometry.Points(g)) r = r.Include(pt);
        return r.IsEmpty ? new Rect2(Position.X, Position.Y, Position.X, Position.Y) : r;
    }

    public override string ToString() => $"{Reference} {Value}";
}

public sealed record Track(Vec2 Start, Vec2 End, double Width, string Layer, string Net, Vec2? Mid = null)
{
    public bool IsArc => Mid is not null;
}

public sealed record Via(Vec2 Position, double Size, double Drill, IReadOnlyList<string> Layers, string Net);

public sealed class Zone
{
    public string Net { get; init; } = "";
    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
    public bool IsRuleArea { get; init; }
    public IReadOnlyList<Vec2> Outline { get; init; } = Array.Empty<Vec2>();
    /// <summary>The filled copper per layer, as KiCad stored it at the last fill.</summary>
    public List<(string Layer, IReadOnlyList<Vec2> Points)> Fills { get; } = new();
}

public sealed class LayerInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public int Ordinal { get; init; }
    public bool IsCopper => Name.EndsWith(".Cu");
}

public sealed class Board
{
    public string FilePath { get; init; } = "";
    public string Title => Path.GetFileNameWithoutExtension(FilePath);
    public string Generator { get; init; } = "";
    public string Version { get; init; } = "";
    public double Thickness { get; init; } = 1.6;
    public List<LayerInfo> Layers { get; } = new();
    public List<Footprint> Footprints { get; } = new();
    public List<Track> Tracks { get; } = new();
    public List<Via> Vias { get; } = new();
    public List<Zone> Zones { get; } = new();
    public List<Shape> Graphics { get; } = new();

    /// <summary>Copper layers from top to bottom: F.Cu, In1.Cu ..., B.Cu.</summary>
    public IReadOnlyList<string> CopperLayers =>
        Layers.Where(l => l.IsCopper)
              .OrderBy(l => l.Name == "F.Cu" ? -1 : l.Name == "B.Cu" ? int.MaxValue : InnerIndex(l.Name))
              .Select(l => l.Name).ToList();

    private static int InnerIndex(string name) =>
        int.TryParse(new string(name.Where(char.IsDigit).ToArray()), out var n) ? n : 0;

    public IEnumerable<Pad> Pads => Footprints.SelectMany(f => f.Pads);

    public Footprint? Find(string reference) =>
        Footprints.FirstOrDefault(f => string.Equals(f.Reference, reference, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<Shape> Outline => Graphics.Where(g => g.Layer == "Edge.Cuts")
        .Concat(Footprints.SelectMany(f => f.Graphics).Where(g => g.Layer == "Edge.Cuts"));

    public Rect2 Bounds()
    {
        var r = Rect2.Empty;
        foreach (var s in Outline) foreach (var p in Geometry.Points(s)) r = r.Include(p);
        if (r.IsEmpty)
            foreach (var f in Footprints) { var b = f.Bounds(); r = r.Include(new(b.MinX, b.MinY)).Include(new(b.MaxX, b.MaxY)); }
        return r.IsEmpty ? new Rect2(0, 0, 100, 100) : r;
    }
}
