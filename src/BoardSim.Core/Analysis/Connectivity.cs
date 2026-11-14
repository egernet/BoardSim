using BoardSim.Core.Model;

namespace BoardSim.Core.Analysis;

/// <summary>
/// Works out which copper actually touches which, per net: pads, tracks, vias
/// and zone fills. A net whose pads end up in more than one island is not fully
/// routed - the copper does not join what the schematic says should be joined.
/// </summary>
public sealed class Connectivity
{
    public sealed record Island(string Net, IReadOnlyList<Pad> Pads);

    /// <summary>Nets whose pads are split over several copper islands.</summary>
    public IReadOnlyList<(string Net, IReadOnlyList<Island> Islands)> Unrouted { get; }

    private Connectivity(List<(string, IReadOnlyList<Island>)> unrouted) => Unrouted = unrouted;

    public static Connectivity Analyze(Board board)
    {
        var copper = board.CopperLayers;
        var result = new List<(string, IReadOnlyList<Island>)>();

        var padsByNet = board.Pads.Where(p => p.HasNet && p.Layers.Any(copper.Contains)).GroupBy(p => p.Net);
        var tracksByNet = board.Tracks.GroupBy(t => t.Net).ToDictionary(g => g.Key, g => g.ToList());
        var viasByNet = board.Vias.GroupBy(v => v.Net).ToDictionary(g => g.Key, g => g.ToList());
        var fillsByNet = board.Zones.Where(z => !z.IsRuleArea && z.Net.Length > 0)
            .SelectMany(z => z.Fills.Select(f => (z.Net, f.Layer, f.Points, Bounds: Geometry.BoundsOf(f.Points))))
            .GroupBy(f => f.Net).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var group in padsByNet)
        {
            var pads = group.ToList();
            if (pads.Count < 2) continue;
            var tracks = tracksByNet.GetValueOrDefault(group.Key) ?? new List<Track>();
            var vias = viasByNet.GetValueOrDefault(group.Key) ?? new List<Via>();
            var fills = fillsByNet.GetValueOrDefault(group.Key) ?? new();

            // Items: pads, then tracks, then vias, then fills.
            var n = pads.Count + tracks.Count + vias.Count + fills.Count;
            var uf = new UnionFind(n);
            int T(int i) => pads.Count + i;
            int V(int i) => pads.Count + tracks.Count + i;
            int F(int i) => pads.Count + tracks.Count + vias.Count + i;

            // Pads of one footprint with the same number are one terminal
            // (e.g. a QFN exposed pad split into several pads).
            for (var i = 0; i < pads.Count; i++)
                for (var j = i + 1; j < pads.Count; j++)
                    if (pads[i].Footprint == pads[j].Footprint && pads[i].Number == pads[j].Number &&
                        pads[i].Bounds().Inflate(0.01).Intersects(pads[j].Bounds()))
                        uf.Union(i, j);

            // Track ends against pads, vias and other tracks.
            for (var ti = 0; ti < tracks.Count; ti++)
            {
                var t = tracks[ti];
                var half = t.Width / 2;
                foreach (var end in new[] { t.Start, t.End })
                {
                    for (var pi = 0; pi < pads.Count; pi++)
                        if (pads[pi].OnLayer(t.Layer) && pads[pi].Contains(end, half * 0.5)) uf.Union(T(ti), pi);
                    for (var vi = 0; vi < vias.Count; vi++)
                        if (vias[vi].Layers.Contains(t.Layer) && vias[vi].Position.DistanceTo(end) <= vias[vi].Size / 2 + half * 0.5)
                            uf.Union(T(ti), V(vi));
                    for (var tj = 0; tj < tracks.Count; tj++)
                    {
                        if (tj == ti || tracks[tj].Layer != t.Layer) continue;
                        var o = tracks[tj];
                        if (Geometry.DistanceToSegment(end, o.Start, o.End) <= (o.Width + t.Width) / 4)
                            uf.Union(T(ti), T(tj));
                    }
                }
            }

            // A track may also run straight across a pad (a shunt part or a
            // test point sitting on the line) without ending in it.
            for (var ti = 0; ti < tracks.Count; ti++)
                for (var pi = 0; pi < pads.Count; pi++)
                {
                    var t = tracks[ti];
                    if (!pads[pi].OnLayer(t.Layer)) continue;
                    var closest = Geometry.ClosestPointOnSegment(pads[pi].Position, t.Start, t.End);
                    if (pads[pi].Contains(closest, t.Width / 2)) uf.Union(T(ti), pi);
                }

            // Vias inside pads (via-in-pad, thermal vias).
            for (var vi = 0; vi < vias.Count; vi++)
                for (var pi = 0; pi < pads.Count; pi++)
                    if (pads[pi].Layers.Any(vias[vi].Layers.Contains) && pads[pi].Contains(vias[vi].Position))
                        uf.Union(V(vi), pi);

            // Zone fills: anything whose copper overlaps the filled polygon.
            for (var fi = 0; fi < fills.Count; fi++)
            {
                var (_, layer, poly, bounds) = fills[fi];
                for (var pi = 0; pi < pads.Count; pi++)
                {
                    var pad = pads[pi];
                    if (!pad.OnLayer(layer) || !bounds.Intersects(pad.Bounds())) continue;
                    if (PadTouchesFill(pad, poly)) uf.Union(F(fi), pi);
                }
                for (var vi = 0; vi < vias.Count; vi++)
                {
                    var v = vias[vi];
                    if (!v.Layers.Contains(layer) || !bounds.Inflate(v.Size).Contains(v.Position)) continue;
                    if (Geometry.PointInPolygon(v.Position, poly) || Geometry.DistanceToPolygonEdge(v.Position, poly) <= v.Size / 2)
                        uf.Union(F(fi), V(vi));
                }
                for (var ti = 0; ti < tracks.Count; ti++)
                {
                    var t = tracks[ti];
                    if (t.Layer != layer) continue;
                    foreach (var end in new[] { t.Start, t.End })
                        if (bounds.Inflate(t.Width).Contains(end) &&
                            (Geometry.PointInPolygon(end, poly) || Geometry.DistanceToPolygonEdge(end, poly) <= t.Width / 2))
                        { uf.Union(F(fi), T(ti)); break; }
                }
            }

            var islands = Enumerable.Range(0, pads.Count).GroupBy(uf.Find)
                .Select(g => new Island(group.Key, g.Select(i => pads[i]).ToList()))
                .ToList();
            if (islands.Count > 1) result.Add((group.Key, islands));
        }

        return new Connectivity(result);
    }

    private static bool PadTouchesFill(Pad pad, IReadOnlyList<Vec2> poly)
    {
        // Solid connection: the fill covers the pad centre.
        if (Geometry.PointInPolygon(pad.Position, poly)) return true;
        // Thermal relief: the spokes reach into the pad, so some fill vertex lies on it.
        var b = pad.Bounds().Inflate(0.05);
        foreach (var p in poly)
            if (b.Contains(p) && pad.Contains(p, 0.05)) return true;
        return false;
    }

    private sealed class UnionFind
    {
        private readonly int[] _parent;
        public UnionFind(int n) { _parent = Enumerable.Range(0, n).ToArray(); }
        public int Find(int x) { while (_parent[x] != x) x = _parent[x] = _parent[_parent[x]]; return x; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _parent[a] = b; }
    }
}
