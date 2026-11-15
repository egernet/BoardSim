using BoardSim.Core.Analysis;
using BoardSim.Core.Model;

namespace BoardSim.Core.Emc;

/// <summary>Copper layer heights from the board's stackup (or a standard stackup when the file has none).</summary>
public sealed class EmcStackup
{
    /// <summary>Copper layer name to its depth below the top surface (mm) and the εr of the dielectric around it.</summary>
    private readonly Dictionary<string, (double Z, double Er)> _layers = new();
    public IReadOnlyList<string> Copper { get; }
    public bool FromFile { get; }
    public double Thickness { get; }

    public EmcStackup(Board board)
    {
        Copper = board.CopperLayers;
        var stack = board.Stackup;
        if (stack.Any(l => l.IsCopper) && stack.Where(l => l.IsDielectric).Sum(l => l.Thickness) > 0.1)
        {
            FromFile = true;
            var z = 0.0; var lastEr = 4.4;
            foreach (var l in stack)
            {
                if (l.IsCopper) _layers[l.Name] = (z, lastEr);
                if (l.IsDielectric) { z += l.Thickness; lastEr = l.EpsilonR; }
                else if (l.IsCopper) z += l.Thickness;
            }
            Thickness = z;
        }
        else
        {
            // JLC's standard stackups: 2 layers 1.6 mm; 4 layers 0.21 / 1.065 / 0.21 mm.
            Thickness = board.Thickness;
            var gaps = Copper.Count == 4 ? new[] { 0.21, 1.065, 0.21 }
                     : Enumerable.Repeat(board.Thickness / Math.Max(1, Copper.Count - 1), Math.Max(0, Copper.Count - 1)).ToArray();
            var z = 0.0;
            for (var i = 0; i < Copper.Count; i++) { _layers[Copper[i]] = (z, 4.4); if (i < gaps.Length) z += gaps[i]; }
        }
    }

    public double Z(string layer) => _layers.TryGetValue(layer, out var v) ? v.Z : 0;
    public double Gap(string a, string b) => Math.Abs(Z(a) - Z(b));
    public double Er(string layer) => _layers.TryGetValue(layer, out var v) ? v.Er : 4.4;

    /// <summary>Copper layers ordered by distance from <paramref name="layer"/>, nearest first (the layer itself excluded).</summary>
    public IEnumerable<string> Neighbours(string layer) => Copper.Where(l => l != layer).OrderBy(l => Gap(l, layer));
}

/// <summary>Filled copper (zones) per layer, to find what a track has underneath it.</summary>
public sealed class PlaneMap
{
    private sealed record Fill(string Net, IReadOnlyList<Vec2> Points, Rect2 Box, double Area);
    private readonly Dictionary<string, List<Fill>> _fills = new();

    public PlaneMap(Board board)
    {
        foreach (var z in board.Zones.Where(z => !z.IsRuleArea && z.Net.Length > 0))
            foreach (var (layer, pts) in z.Fills)
            {
                if (!_fills.TryGetValue(layer, out var list)) _fills[layer] = list = new();
                list.Add(new Fill(z.Net, pts, Geometry.BoundsOf(pts), Math.Abs(SignedArea(pts))));
            }
    }

    /// <summary>The net of the copper fill at <paramref name="p"/> on a layer, or null.</summary>
    public string? NetAt(string layer, Vec2 p)
    {
        if (!_fills.TryGetValue(layer, out var list)) return null;
        foreach (var f in list)
            if (f.Box.Contains(p) && Geometry.PointInPolygon(p, f.Points)) return f.Net;
        return null;
    }

    /// <summary>Total filled area of a net on a layer, in mm².</summary>
    public double Area(string layer, string net) =>
        _fills.TryGetValue(layer, out var list) ? list.Where(f => f.Net == net).Sum(f => f.Area) : 0;

    /// <summary>Layers with large fills of a net (planes), with their area.</summary>
    public IEnumerable<(string Layer, string Net, double Area)> Planes(double minArea = 400) =>
        _fills.SelectMany(kv => kv.Value.GroupBy(f => f.Net).Select(g => (kv.Key, g.Key, g.Sum(f => f.Area))))
              .Where(x => x.Item3 >= minArea);

    private static double SignedArea(IReadOnlyList<Vec2> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X + p[i].X) * (p[j].Y - p[i].Y);
        return a / 2;
    }
}

/// <summary>A stretch of a net's track with the same reference underneath.</summary>
public sealed record RouteRun(string Layer, double Length, string? Reference, string? ReferenceLayer, double Height, double Width);

/// <summary>A via where the net's reference plane changes, and how far the return current has to go to follow.</summary>
public sealed record ReferenceChange(Vec2 At, string From, string To, double StitchDistance);

/// <summary>How one net is routed, seen from its return current.</summary>
public sealed class NetRoute
{
    public required string Net { get; init; }
    public List<RouteRun> Runs { get; } = new();
    public int Vias { get; set; }
    public List<ReferenceChange> Changes { get; } = new();
    public List<Track> Tracks { get; } = new();

    public double Length => Runs.Sum(r => r.Length);
    public double Unreferenced => Runs.Where(r => r.Reference is null).Sum(r => r.Length);
    /// <summary>Area between the track and its return path, in mm² (length x height to the reference).</summary>
    public double LoopArea => Runs.Sum(r => r.Length * r.Height);
    public IReadOnlyDictionary<string, double> ByLayer => Runs.GroupBy(r => r.Layer).ToDictionary(g => g.Key, g => g.Sum(r => r.Length));
    public IReadOnlyDictionary<string, double> ByReference =>
        Runs.GroupBy(r => r.Reference ?? "(none)").ToDictionary(g => g.Key, g => g.Sum(r => r.Length));
    /// <summary>The layer most of the net runs on.</summary>
    public string MainLayer => Runs.GroupBy(r => r.Layer).OrderByDescending(g => g.Sum(r => r.Length)).FirstOrDefault()?.Key ?? "F.Cu";
    public double TypicalHeight => Length > 0 ? Runs.Sum(r => r.Height * r.Length) / Length : 0.2;
    public double TypicalWidth => Length > 0 ? Runs.Sum(r => r.Width * r.Length) / Length : 0.2;
}

/// <summary>Measures how nets are routed: length per layer, the plane under each piece, vias and reference changes.</summary>
public sealed class RouteAnalyzer
{
    public const double Step = 0.5;
    private readonly Board _board;
    public EmcStackup Stackup { get; }
    public PlaneMap Planes { get; }
    private readonly Dictionary<string, NetRoute> _cache = new();

    public RouteAnalyzer(Board board)
    {
        _board = board;
        Stackup = new EmcStackup(board);
        Planes = new PlaneMap(board);
    }

    public NetRoute Route(string net)
    {
        if (_cache.TryGetValue(net, out var cached)) return cached;
        var route = new NetRoute { Net = net };
        foreach (var t in _board.Tracks.Where(t => t.Net == net))
        {
            route.Tracks.Add(t);
            var pts = t.IsArc ? Geometry.ArcPoints(t.Start, t.Mid!.Value, t.End, 12) : new[] { t.Start, t.End };
            for (var i = 1; i < pts.Count; i++) Sample(route, t, pts[i - 1], pts[i]);
        }
        foreach (var v in _board.Vias.Where(v => v.Net == net))
        {
            route.Vias++;
            var refs = v.Layers.Where(l => Stackup.Copper.Contains(l))
                .Select(l => Reference(l, v.Position, net).Net).Where(r => r is not null).Distinct().ToList();
            if (refs.Count > 1)
            {
                // The return current has to change plane here: through a via if both planes are the same net,
                // otherwise through the nearest capacitor between the two nets.
                var stitch = refs.Distinct().Count() == 1 ? 0 : NearestStitch(v.Position, refs!);
                route.Changes.Add(new ReferenceChange(v.Position, refs[0]!, refs[1]!, stitch));
            }
        }
        _cache[net] = route;
        return route;
    }

    private void Sample(NetRoute route, Track t, Vec2 a, Vec2 b)
    {
        var len = a.DistanceTo(b);
        var n = Math.Max(1, (int)Math.Ceiling(len / Step));
        RouteRun? run = null;
        for (var i = 0; i < n; i++)
        {
            var p = a + (b - a) * ((i + 0.5) / n);
            var (refNet, refLayer, h) = Reference(t.Layer, p, t.Net);
            var piece = len / n;
            if (run is not null && run.Reference == refNet && run.ReferenceLayer == refLayer)
                run = run with { Length = run.Length + piece };
            else
            {
                if (run is not null) route.Runs.Add(run);
                run = new RouteRun(t.Layer, piece, refNet, refLayer, h, t.Width);
            }
        }
        if (run is not null) route.Runs.Add(run);
    }

    /// <summary>The nearest plane under (or over) a point: its net, layer and distance. Height is the board thickness when there is none.</summary>
    public (string? Net, string? Layer, double Height) Reference(string layer, Vec2 p, string ownNet)
    {
        foreach (var other in Stackup.Neighbours(layer))
            if (Planes.NetAt(other, p) is { } net && net != ownNet)
                return (net, other, Math.Max(0.05, Stackup.Gap(layer, other)));
        return (null, null, Math.Max(Stackup.Thickness, 0.8));
    }

    private double NearestStitch(Vec2 at, IReadOnlyList<string?> nets)
    {
        // A capacitor between the two reference nets lets the return current cross.
        var best = double.MaxValue;
        foreach (var fp in _board.Footprints.Where(f => f.Prefix == "C"))
        {
            var padNets = fp.Pads.Select(p => p.Net).Distinct().ToList();
            if (nets.All(n => n is not null && padNets.Contains(n)))
                best = Math.Min(best, fp.Pads.Min(p => p.Position.DistanceTo(at)));
        }
        return best;
    }

    /// <summary>Distance from a point to the nearest via of a net (stitching), or infinity.</summary>
    public double NearestVia(Vec2 p, string net) =>
        _board.Vias.Where(v => v.Net == net).Select(v => v.Position.DistanceTo(p)).DefaultIfEmpty(double.PositiveInfinity).Min();
}

/// <summary>Transmission-line helpers (IPC-2141 microstrip and stripline approximations).</summary>
public static class Lines
{
    public const double C0 = 299_792_458;

    /// <summary>Characteristic impedance of a trace w mm wide, h mm above its plane, t mm thick.</summary>
    public static double Z0(double w, double h, double er, double t = 0.035, bool stripline = false)
    {
        if (h <= 0 || w <= 0) return 100;
        if (stripline) return 60 / Math.Sqrt(er) * Math.Log(4 * (2 * h + t) / (0.67 * Math.PI * (0.8 * w + t)));
        var z = 87 / Math.Sqrt(er + 1.41) * Math.Log(5.98 * h / (0.8 * w + t));
        return Math.Clamp(z, 20, 150);
    }

    /// <summary>Effective εr of a microstrip (part of the field runs in air).</summary>
    public static double ErEff(double er, double w, double h) => (er + 1) / 2 + (er - 1) / 2 / Math.Sqrt(1 + 12 * h / Math.Max(w, 0.01));

    /// <summary>Capacitance per mm of a trace, in farads.</summary>
    public static double CapPerMm(double z0, double erEff) => Math.Sqrt(erEff) / (C0 * z0) * 1e-3;
}
