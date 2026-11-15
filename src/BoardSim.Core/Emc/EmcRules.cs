using System.Globalization;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;

namespace BoardSim.Core.Emc;

internal static class EmcText
{
    public static string Mm(double v) => v.ToString("0", CultureInfo.InvariantCulture) + " mm";
    public static string Hz(double hz) => hz >= 1e6 ? (hz / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + " MHz" : (hz / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";
    public const string Estimate = " (Estimate from the layout and the part data; a real measurement can differ by 10-20 dB.)";
}

/// <summary>Switching signals have a plane under them all the way, preferably ground, and a nearby path where they change layer.</summary>
public sealed class ReturnPathRule : IRule
{
    public string Id => "emc-return-path";
    public string Description => "Switching signals run over a plane, preferably ground, with a return path where they change layer.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var emc = EmcAnalysis.For(ctx.Circuit, ctx.Expectations);
        // Slow edges (over 10 ns) hardly radiate; one finding per mux group, since its nets share the problem.
        foreach (var g in emc.Active.Where(a => !a.Signal.Sine && a.Route.Length > 0 && a.EdgeNs < 10).GroupBy(a => a.Signal.Group ?? a.Net.Name))
        {
            var items = g.Select(a => (a, Issues(ctx, a))).Where(x => x.Item2.Unref > 3 || x.Item2.NonGround.Count > 0 || x.Item2.Far.Count > 0).ToList();
            if (items.Count == 0) continue;
            var notes = new List<string>();
            var unref = items.Select(x => x.Item2.Unref).Where(u => u > 3).ToList();
            if (unref.Count > 0)
                notes.Add($"{Range(unref)} {(items.Count > 1 ? "per net " : "")}has no plane underneath, so the return current spreads out and the loop grows");
            foreach (var plane in items.SelectMany(x => x.Item2.NonGround).GroupBy(x => x.Net))
                notes.Add($"{Range(plane.Select(p => p.Length).ToList())} runs over the {plane.Key.TrimStart('/')} plane: the return current has to cross to ground through a capacitor, and that plane may carry noisy supply current");
            var far = items.SelectMany(x => x.Item2.Far).ToList();
            if (far.Count > 0)
                notes.Add($"{far.Count} via{(far.Count == 1 ? "" : "s")} change the reference between {far[0].From.TrimStart('/')} and {far[0].To.TrimStart('/')} with no capacitor between them within 5 mm");
            var first = items[0].a;
            var severity = items.Any(x => x.Item2.Unref > 25 && x.a.EdgeNs < 5) ? Severity.Warning : Severity.Info;
            var name = items.Count == 1 ? first.Net.DisplayName : $"{items.Count} nets after {g.Key.Replace(" outputs", "")}";
            yield return new Finding
            {
                Severity = severity, Rule = Id,
                Title = $"{name}: return path ({first.Signal.Name}, {EmcText.Hz(first.Signal.Hz)})",
                Detail = string.Join(". ", notes) + (items.Count == 1
                    ? $". The net is {EmcText.Mm(first.Route.Length)} long with {first.Route.Vias} vias; loop area about {first.Route.LoopArea.ToString("0.#", CultureInfo.InvariantCulture)} mm²."
                    : $". Nets: {string.Join(", ", items.Select(x => x.a.Net.DisplayName))}."),
                Nets = items.Select(x => x.a.Net.Name).ToList(),
                Location = first.Route.Tracks.FirstOrDefault()?.Start,
            };
        }
    }

    private static (double Unref, List<(string Net, double Length)> NonGround, List<ReferenceChange> Far) Issues(CheckContext ctx, ActiveNet a)
    {
        var r = a.Route;
        var nonGround = r.ByReference.Where(kv => kv.Key != "(none)" && ctx.Circuit.Nets.TryGetValue(kv.Key, out var n) && n.Volts is not 0)
            .Where(kv => kv.Value > 5 && kv.Value > 0.2 * r.Length).Select(kv => (kv.Key, kv.Value)).ToList();
        return (r.Unreferenced, nonGround, r.Changes.Where(c => c.From != c.To && c.StitchDistance > 5).ToList());
    }

    private static string Range(IReadOnlyList<double> mm)
    {
        var lo = mm.Min(); var hi = mm.Max();
        return Math.Round(lo) == Math.Round(hi) ? EmcText.Mm(hi) : $"{lo:0}-{EmcText.Mm(hi)}";
    }
}

/// <summary>Differential pairs (USB) are routed together: equal length, same layers.</summary>
public sealed class DiffPairRule : IRule
{
    public string Id => "emc-diff-pair";
    public string Description => "USB D+ and D- have about the same length and the same layer changes.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var emc = EmcAnalysis.For(ctx.Circuit, ctx.Expectations);
        var usb = emc.Active.Where(a => a.Signal.Name.StartsWith("USB D") && a.LeavesBoard).ToList();
        foreach (var plus in usb.Where(a => a.Signal.Name.EndsWith("+")))
        {
            var connector = plus.Exits[0].Part;
            var minus = usb.FirstOrDefault(a => a.Signal.Name.EndsWith("-") && a.Exits.Any(e => e.Part == connector));
            if (minus is null) continue;
            // The whole path from the chip: the nets the signal passes through on its way.
            var lp = PathLength(emc, plus); var lm = PathLength(emc, minus);
            var vp = PathVias(emc, plus); var vm = PathVias(emc, minus);
            var diff = Math.Abs(lp - lm);
            var skewPs = diff / (Lines.C0 * 1e3 / Math.Sqrt(3.0)) * 1e12;
            var layersP = string.Join("/", plus.Route.ByLayer.Keys.OrderBy(k => k));
            var layersM = string.Join("/", minus.Route.ByLayer.Keys.OrderBy(k => k));
            if (diff < 5 && vp == vm && layersP == layersM) continue;
            yield return new Finding
            {
                Severity = Severity.Info, Rule = Id,
                Title = $"USB pair on {connector.Reference}: {EmcText.Mm(lp)} / {EmcText.Mm(lm)}, {vp} / {vm} vias",
                Detail = $"D+ and D- differ by {diff.ToString("0.#", CultureInfo.InvariantCulture)} mm (about {skewPs:0} ps of skew)" +
                         (layersP != layersM ? $" and run on different layers ({layersP} against {layersM})" : "") +
                         ". Full speed (12 Mbit/s) tolerates this; mismatch turns part of the signal into common-mode current on the cable, " +
                         "which is what radiates. Route them side by side on one layer over ground.",
                Nets = new[] { plus.Net.Name, minus.Net.Name },
                Refs = new[] { connector.Reference },
            };
        }
    }

    private static double PathLength(EmcAnalysis emc, ActiveNet a) =>
        emc.Circuit.SeriesNets(a.Net).Sum(n => emc.Routes.Route(n.Name).Length);
    private static int PathVias(EmcAnalysis emc, ActiveNet a) =>
        emc.Circuit.SeriesNets(a.Net).Sum(n => emc.Routes.Route(n.Name).Vias);
}

/// <summary>A TVS clamps best right at the connector: every mm of track before it adds inductance.</summary>
public sealed class EsdDistanceRule : IRule
{
    public double MaxDistance { get; init; } = 10;
    public string Id => "emc-esd-distance";
    public string Description => "ESD/TVS diodes sit close to the connector pins they protect.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var tvs in c.Parts.Where(p => p.Kind == PartKind.Tvs))
        {
            var far = new List<(PinRef Tvs, PinRef Conn, double D)>();
            foreach (var pin in tvs.Pins.Where(p => !p.Net.IsRail))
                foreach (var conn in pin.Net.Pins.Where(p => p.Part.Kind == PartKind.Connector))
                {
                    var d = pin.Pad.Position.DistanceTo(conn.Pad.Position);
                    if (d > MaxDistance) far.Add((pin, conn, d));
                }
            if (far.Count == 0) continue;
            var worst = far.Max(f => f.D);
            yield return new Finding
            {
                Severity = Severity.Info, Rule = Id,
                Title = $"{tvs.Reference} is {(Math.Round(far.Min(f => f.D)) == Math.Round(worst) ? EmcText.Mm(worst) : $"{far.Min(f => f.D):0}-{EmcText.Mm(worst)}")} from the connector pins it protects",
                Detail = $"An ESD pulse travels that far over the board before {tvs.Reference} clamps it. At about 1 nH per mm that is " +
                         $"{worst:0} nH, or roughly {Math.Round(worst) * 10:0} V of extra overshoot on a 1 ns, 10 A ESD edge. Place the TVS at the connector " +
                         $"({string.Join(", ", far.Select(f => f.Conn.Part.Reference).Distinct())}).",
                Refs = far.SelectMany(f => new[] { f.Tvs.Id, f.Conn.Id }).Distinct().ToList(),
                Nets = far.Select(f => f.Tvs.Net.Name).Distinct().ToList(),
                Location = far[0].Tvs.Pad.Position,
            };
        }
    }
}

/// <summary>The radiated emission estimate stays below CISPR 32 class B.</summary>
public sealed class EmissionRule : IRule
{
    public string Id => "emc-emission";
    public string Description => "Estimated radiated emission stays below the CISPR 32 class B limit (3 m).";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var emc = EmcAnalysis.For(ctx.Circuit, ctx.Expectations);
        foreach (var g in emc.Active.GroupBy(a => a.Signal.Group ?? a.Net.Name))
        {
            var (a, peak) = g.Select(x => (x, emc.Peak(x))).OrderBy(x => x.Item2.Margin).First();
            if (peak.Margin > 6) continue;
            var dm = emc.DifferentialMode(a, peak.Hz);
            var cm = emc.CommonMode(a, peak.Hz);
            var main = cm > dm ? $"the cable on {string.Join(", ", a.Exits.Select(e => e.Part.Reference).Distinct())} (common mode, {a.CableMetres:0.#} m assumed)" : "the loop between the track and its return path";
            yield return new Finding
            {
                Severity = peak.Margin < 0 ? Severity.Warning : Severity.Info, Rule = Id,
                Title = $"{(g.Count() > 1 ? g.Key : a.Net.DisplayName)}: about {peak.Level:0} dBµV/m at {EmcText.Hz(peak.Hz)}, " +
                        (peak.Margin < 0 ? $"{-peak.Margin:0} dB over" : $"{peak.Margin:0} dB under") + " the class B limit",
                Detail = $"{a.Signal.Name} ({EmcText.Hz(a.Signal.Hz)}, {a.EdgeNs:0.#} ns edges, {a.CurrentA * 1000:0} mA) radiates mostly through {main}. " +
                         "Slower edges (series resistor), a shorter loop over ground or a ferrite on the cable bring it down." + EmcText.Estimate,
                Nets = g.Select(x => x.Net.Name).ToList(),
                Location = a.Route.Tracks.FirstOrDefault()?.Start,
            };
        }
    }
}

/// <summary>Crosstalk from switching signals onto neighbouring tracks stays small.</summary>
public sealed class CrosstalkRule : IRule
{
    public double Threshold { get; init; } = 5;
    public string Id => "emc-crosstalk";
    public string Description => "Switching signals couple less than about 5 % onto neighbouring tracks.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var emc = EmcAnalysis.For(ctx.Circuit, ctx.Expectations);
        foreach (var x in emc.Crosstalk().Where(x => x.Percent >= Threshold))
            yield return new Finding
            {
                Severity = Severity.Info, Rule = Id,
                Title = $"{x.Aggressor.Net.DisplayName} couples about {x.Percent:0} % onto {x.Victim.DisplayName}",
                Detail = $"They run {EmcText.Mm(x.ParallelMm)} side by side on {x.Layer}, {x.GapMm.ToString("0.##", CultureInfo.InvariantCulture)} mm apart. " +
                         "More space (3x the height to the plane) or a ground track between them reduces it." + EmcText.Estimate,
                Nets = new[] { x.Aggressor.Net.Name, x.Victim.Name },
            };
    }
}
