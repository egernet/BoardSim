using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Emc;

/// <summary>A switching signal: a trapezoid (or a sine) with the driver's edge and output resistance.</summary>
public sealed record Signal(string Name, double Hz, double Duty, double RiseNs, double Volts, double SourceOhms, bool Sine, string Why, string Data)
{
    /// <summary>Nets in the same group are never active at once (the outputs of one analog mux).</summary>
    public string? Group { get; init; }
}

/// <summary>A net carrying a known signal, with everything the estimates need.</summary>
public sealed class ActiveNet
{
    public required Net Net { get; init; }
    public required Signal Signal { get; init; }
    public PinRef? Driver { get; init; }
    public required NetRoute Route { get; init; }
    /// <summary>Edge time on this net after series resistance and load, in ns.</summary>
    public double EdgeNs { get; init; }
    /// <summary>Peak current of an edge, in A.</summary>
    public double CurrentA { get; init; }
    public double Z0 { get; init; }
    /// <summary>Connector pins the net leaves the board through.</summary>
    public IReadOnlyList<PinRef> Exits { get; init; } = Array.Empty<PinRef>();
    public double CableMetres { get; init; }
    /// <summary>How much the cable's shield cuts its common-mode emission, in dB (from the connector's part model).</summary>
    public double CableShieldDb { get; init; }
    public bool LeavesBoard => Exits.Count > 0;
}

public sealed record CrosstalkPair(ActiveNet Aggressor, Net Victim, string Layer, double ParallelMm, double GapMm, double Percent);

public sealed class EmcOptions
{
    /// <summary>Distance to the receiving antenna, in m (CISPR 32 class B uses 3 m).</summary>
    public double Distance { get; init; } = 3;
    /// <summary>Share of a cable signal's current that flows as common-mode current (an assumption, a few %).</summary>
    public double CommonModeFraction { get; init; } = 0.01;
    /// <summary>Cable length when the board does not say, in m.</summary>
    public double DefaultCable { get; init; } = 1;
}

/// <summary>
/// Noise and emission estimates from the board geometry and the real parts:
/// which nets switch, how fast (from the driver's part model), how the
/// return current flows (from the stackup and the planes), and roughly how
/// much that radiates. These are engineering estimates, typically within
/// 10-20 dB of a chamber measurement: good for comparing layouts and finding
/// the worst nets, not for certification.
/// </summary>
public sealed class EmcAnalysis
{
    public Circuit Circuit { get; }
    public RouteAnalyzer Routes { get; }
    public EmcOptions Options { get; }
    public IReadOnlyList<ActiveNet> Active { get; }
    public double[] Frequencies { get; }

    private static readonly ConditionalWeakTable<Circuit, EmcAnalysis> Cache = new();

    public static EmcAnalysis For(Circuit circuit, Expectations? expectations = null) =>
        Cache.GetValue(circuit, c => new EmcAnalysis(c, expectations, new EmcOptions()));

    public EmcAnalysis(Circuit circuit, Expectations? expectations, EmcOptions options,
        IReadOnlyDictionary<string, (double Hz, double Duty)>? measured = null)
    {
        Circuit = circuit;
        Options = options;
        Routes = new RouteAnalyzer(circuit.Board);
        Frequencies = Enumerable.Range(0, 361).Select(i => 30e6 * Math.Pow(1000.0 / 30, i / 360.0)).ToArray();
        Active = FindActive(expectations, measured);
    }

    // ------------------------------------------------------------------ signals

    private List<ActiveNet> FindActive(Expectations? exp, IReadOnlyDictionary<string, (double Hz, double Duty)>? measured)
    {
        var found = new Dictionary<Net, ActiveNet>();
        var cables = exp?.Cables ?? new Dictionary<string, double>();

        void Start(PinRef? driver, Net net, string name, double hz, double duty, double? riseNs, string why, bool sine = false)
        {
            var (rise, ohms, data) = Edge(driver);
            if (riseNs is { } r) { rise = r; data = "boardsim.json"; }
            var volts = driver?.Part.SupplyVolts() ?? 3.3;
            if (sine) { volts = 0.5; ohms = 100; data = "crystal oscillator, about 1 Vpp"; }
            Spread(net, driver, new Signal(name, hz, duty, rise, volts, ohms, sine, why, data), found, cables);
        }

        if (exp is not null)
            foreach (var s in exp.Signals.Where(s => s.Hz > 0))
            {
                var pin = ExpectationRule.Resolve(Circuit, s.Pin);
                var net = pin?.Net ?? Circuit.Nets.Values.FirstOrDefault(n => string.Equals(n.DisplayName, s.Pin.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                if (net is null) continue;
                Start(pin, net, s.Why ?? s.Pin, s.Hz, s.Duty, s.RiseNs, s.Why ?? "from boardsim.json");
            }

        if (measured is not null)
            foreach (var (pinId, (hz, duty)) in measured)
                if (ExpectationRule.Resolve(Circuit, pinId) is { } pin && hz > 0)
                    Start(pin, pin.Net, $"{pin.Label} (measured)", hz, duty, null, "measured in the running firmware");

        foreach (var part in Circuit.Parts)
        {
            // USB data lines: nets that reach a connector's D+/D- pins, driven by a chip.
            if (part.Kind == PartKind.Connector)
                foreach (var pin in part.Pins.Where(p => Regex.IsMatch(p.Name, @"^(D[+-]|DP|DN|USB_?D[PNM+-])$", RegexOptions.IgnoreCase)))
                {
                    var driver = Circuit.SeriesNets(pin.Net).SelectMany(n => n.Pins)
                        .FirstOrDefault(p => p.Part.Kind == PartKind.Ic && p.Model?.Type is PinType.Io or PinType.Out);
                    Start(driver, pin.Net, $"USB {pin.Name}", 12e6, 0.5, null, "USB full speed (12 Mbit/s)");
                }

            // Crystals: a sine at the chip's crystal frequency on the XTAL pins.
            if (part.Model?.CrystalHz is { } xtal)
                foreach (var pin in part.Pins.Where(p => p.Name.StartsWith("XTAL", StringComparison.OrdinalIgnoreCase)))
                    foreach (var net in Circuit.SeriesNets(pin.Net, 1).Where(n => !n.IsRail))
                        Start(pin, net, "crystal", xtal, 0.5, null, "crystal oscillator", sine: true);

            // UART TX at 115200 baud (worst case: alternating bits).
            if (part.Kind == PartKind.Ic)
                foreach (var pin in part.Pins.Where(p => Regex.IsMatch(p.Name, @"^(U0)?TXD?0?$", RegexOptions.IgnoreCase) ||
                                                         Regex.IsMatch(p.Net.DisplayName, @"(^|/)(U\d?_?)?TXD?$", RegexOptions.IgnoreCase)))
                    if (pin.Model?.Type is PinType.Out or PinType.Io or null)
                        Start(pin, pin.Net, "UART TX", 115200 / 2.0, 0.5, null, "UART at 115200 baud");
        }
        return found.Values.OrderBy(a => a.Net.DisplayName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Edge time and output resistance of a driver, from its pin or part model.</summary>
    private static (double RiseNs, double Ohms, string Data) Edge(PinRef? driver)
    {
        var m = driver?.Part.Model;
        if (driver?.Model?.RiseNs is { } pr) return (pr, driver.Model.DriveOhms ?? m?.DriveOhms ?? 30, $"{m?.Name ?? driver.Part.Reference} pin data");
        if (m?.RiseNs is { } r) return (r, m.DriveOhms ?? 30, m.EdgeSource ?? $"{m.Name} part model");
        return (5, 30, driver is null ? "generic (no driver found)" : $"generic ({driver.Part.Reference} has no edge data)");
    }

    /// <summary>
    /// Follows a signal from its net through series resistors, analog switches
    /// and buffers. A buffer starts a new edge (its own rise time and drive);
    /// resistors and switches add source resistance.
    /// </summary>
    private void Spread(Net start, PinRef? driver, Signal signal, Dictionary<Net, ActiveNet> found, Dictionary<string, double> cables)
    {
        var queue = new Queue<(Net Net, Signal Sig, PinRef? Drv, int Depth)>();
        var seen = new HashSet<Net>();
        queue.Enqueue((start, signal, driver, 0));
        while (queue.Count > 0)
        {
            var (net, sig, drv, depth) = queue.Dequeue();
            if (net.IsRail || net.IsNoConnect || !seen.Add(net)) continue;
            // The first signal found for a net wins; a square wave replaces a sine (it radiates more).
            if (!found.TryGetValue(net, out var have) || (have.Signal.Sine && !sig.Sine))
                found[net] = Make(net, sig, drv, cables);
            if (depth > 6) continue;
            foreach (var pin in net.Pins)
            {
                var part = pin.Part;
                if (part.Kind == PartKind.Resistor && part.IsTwoTerminal && part.Other(net) is { } other && part.Value is { } ohms && ohms < 10_000)
                    queue.Enqueue((other, sig with { SourceOhms = sig.SourceOhms + ohms }, drv, depth + 1));
                if (part.Model is null || pin.Model is null) continue;
                foreach (var pair in part.Model.Through.Where(t => t.Length == 2))
                {
                    if (!string.Equals(pair[0], pin.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var outPin in part.PinsNamed(pair[1]))
                    {
                        if (part.Model.Behavior?.Type == "buffer")
                        {
                            var (rise, drive, data) = Edge(outPin);
                            queue.Enqueue((outPin.Net, sig with { RiseNs = rise, SourceOhms = drive, Volts = part.SupplyVolts() ?? sig.Volts, Data = data }, outPin, depth + 1));
                        }
                        else
                            queue.Enqueue((outPin.Net, sig with
                            {
                                SourceOhms = sig.SourceOhms + (part.Model.Behavior?.OnResistance ?? 50),
                                Group = part.Model.Behavior?.Type == "analogMux" ? $"{part.Reference} outputs" : sig.Group,
                            }, drv, depth + 1));
                    }
                }
            }
        }
    }

    private ActiveNet Make(Net net, Signal sig, PinRef? driver, Dictionary<string, double> cables)
    {
        var route = Routes.Route(net.Name);
        var layer = route.MainLayer;
        var h = route.Length > 0 ? route.TypicalHeight : 0.2;
        var w = route.Length > 0 ? route.TypicalWidth : 0.2;
        var er = Routes.Stackup.Er(layer);
        var inner = layer != "F.Cu" && layer != "B.Cu";
        var z0 = Lines.Z0(w, h, er, stripline: inner);
        var erEff = inner ? er : Lines.ErEff(er, w, h);
        // Load: the trace plus about 5 pF per pin. Series resistance slows the edge (RC).
        var c = route.Length * Lines.CapPerMm(z0, erEff) + net.Pins.Count * 5e-12;
        var rcNs = 2.2 * Math.Max(0, sig.SourceOhms - 30) * c * 1e9;
        var edge = sig.Sine ? sig.RiseNs : Math.Sqrt(sig.RiseNs * sig.RiseNs + rcNs * rcNs);
        var exits = net.Pins.Where(p => p.Part.Kind == PartKind.Connector && p.Part.Model?.Id != "raspberry-pi-40").ToList();
        var cable = exits.Select(p => cables.TryGetValue(p.Part.Reference, out var m) ? m : Options.DefaultCable).DefaultIfEmpty(0).Max();
        var shield = exits.Select(p => p.Part.Model?.CableShieldDb ?? 0).DefaultIfEmpty(0).Min();
        return new ActiveNet
        {
            Net = net, Signal = sig, Driver = driver, Route = route, EdgeNs = edge, Z0 = z0,
            CurrentA = sig.Volts / (sig.SourceOhms + z0), Exits = exits, CableMetres = cable, CableShieldDb = shield,
        };
    }

    // ------------------------------------------------------------------ emission

    /// <summary>CISPR 32 class B radiated limit at 3 m (quasi-peak), dBµV/m.</summary>
    public static double Limit(double hz) => hz < 230e6 ? 40 : 47;

    /// <summary>Amplitude of the signal's harmonics around a frequency (the trapezoid envelope), in A.</summary>
    public static double CurrentAt(ActiveNet a, double hz)
    {
        var s = a.Signal;
        if (s.Sine) return Math.Abs(hz / s.Hz - 1) < 0.012 ? a.CurrentA : 0;
        var n = hz / s.Hz;
        var d = Math.Clamp(s.Duty, 0.01, 0.99);
        var tr = Math.Max(a.EdgeNs, 0.1) * 1e-9;
        return 2 * a.CurrentA * d * Math.Min(1, 1 / (Math.PI * n * d)) * Math.Min(1, 1 / (Math.PI * hz * tr));
    }

    /// <summary>Differential-mode field from the current loop (track + return path), dBµV/m.</summary>
    public double DifferentialMode(ActiveNet a, double hz)
    {
        var i = CurrentAt(a, hz);
        var area = a.Route.LoopArea * 1e-6;
        var e = 1.316e-14 * hz * hz * area * i / Options.Distance;
        return Db(e);
    }

    /// <summary>Common-mode field from a cable on the net's connector, dBµV/m (assumes a fixed common-mode share).</summary>
    public double CommonMode(ActiveNet a, double hz)
    {
        if (!a.LeavesBoard) return double.NegativeInfinity;
        var i = CurrentAt(a, hz) * Options.CommonModeFraction;
        var length = Math.Min(a.CableMetres, Lines.C0 / hz / 4);
        var e = 1.257e-6 * hz * length * i / Options.Distance;
        return Db(e) - a.CableShieldDb;
    }

    public double Total(ActiveNet a, double hz) => PowerSum(DifferentialMode(a, hz), CommonMode(a, hz));

    /// <summary>All nets together at one frequency. Nets of one mux group count once (the loudest), since only one is active at a time.</summary>
    public double Total(double hz) => PowerSum(Active.GroupBy(a => a.Signal.Group ?? a.Net.Name)
        .Select(g => g.Max(a => Total(a, hz))).ToArray());

    /// <summary>The highest point of a net's spectrum and its margin to the limit.</summary>
    public (double Hz, double Level, double Margin) Peak(ActiveNet a)
    {
        var best = (Hz: 0.0, Level: double.NegativeInfinity, Margin: double.PositiveInfinity);
        foreach (var f in Frequencies.Concat(Harmonics(a)))
        {
            var level = Total(a, f);
            var margin = Limit(f) - level;
            if (margin < best.Margin) best = (f, level, margin);
        }
        return best;
    }

    private IEnumerable<double> Harmonics(ActiveNet a)
    {
        if (a.Signal.Sine) { if (a.Signal.Hz is >= 30e6 and <= 1e9) yield return a.Signal.Hz; yield break; }
    }

    public static double Db(double voltsPerMetre) => voltsPerMetre <= 0 ? double.NegativeInfinity : 20 * Math.Log10(voltsPerMetre / 1e-6);

    public static double PowerSum(params double[] db)
    {
        var p = db.Where(d => !double.IsNegativeInfinity(d)).Sum(d => Math.Pow(10, d / 10));
        return p <= 0 ? double.NegativeInfinity : 10 * Math.Log10(p);
    }

    // ------------------------------------------------------------------ crosstalk

    private List<CrosstalkPair>? _crosstalk;

    /// <summary>Backward crosstalk from active nets onto neighbouring tracks on the same layer.</summary>
    public IReadOnlyList<CrosstalkPair> Crosstalk()
    {
        if (_crosstalk is not null) return _crosstalk;
        var result = new Dictionary<(Net, string), CrosstalkPair>();
        var tracks = Circuit.Board.Tracks.Where(t => !t.IsArc && t.Start.DistanceTo(t.End) > 0.5).ToList();
        foreach (var a in Active.Where(a => !a.Signal.Sine))
        {
            var er = Routes.Stackup.Er(a.Route.MainLayer);
            var v = Lines.C0 * 1e3 / Math.Sqrt(Lines.ErEff(er, a.Route.TypicalWidth, a.Route.TypicalHeight)); // mm/s
            foreach (var t in a.Route.Tracks.Where(t => !t.IsArc))
                foreach (var o in tracks)
                {
                    if (o.Net == t.Net || o.Layer != t.Layer || o.Net.Length == 0) continue;
                    if (!Circuit.Nets.TryGetValue(o.Net, out var victim) || victim.IsRail) continue;
                    var (overlap, dist) = Parallel(t, o);
                    if (overlap < 1) continue;
                    var gap = dist - (t.Width + o.Width) / 2;
                    var h = Math.Max(0.05, Routes.Reference(t.Layer, (t.Start + t.End) * 0.5, t.Net).Height);
                    if (gap > 5 * h || gap < 0) continue;
                    var k = 1 / (1 + dist / h * (dist / h));
                    var saturation = Math.Min(1, 2 * overlap / (v * Math.Max(a.EdgeNs, 0.1) * 1e-9));
                    var pct = 100 * k / 4 * saturation;
                    var key = (victim, a.Net.Name);
                    if (result.TryGetValue(key, out var have))
                        result[key] = have with { ParallelMm = have.ParallelMm + overlap, Percent = Math.Min(100, Math.Max(have.Percent, pct) + 0.5 * Math.Min(have.Percent, pct)) , GapMm = Math.Min(have.GapMm, gap) };
                    else result[key] = new CrosstalkPair(a, victim, t.Layer, overlap, gap, pct);
                }
        }
        return _crosstalk = result.Values.OrderByDescending(p => p.Percent).ToList();
    }

    /// <summary>Overlap length and centre distance of two nearly parallel segments (0 when they are not parallel).</summary>
    private static (double Overlap, double Distance) Parallel(Track a, Track b)
    {
        var da = a.End - a.Start; var la = da.Length;
        var db = b.End - b.Start; var lb = db.Length;
        if (la == 0 || lb == 0) return (0, 0);
        var ua = da * (1 / la);
        var cos = Math.Abs((ua.X * db.X + ua.Y * db.Y) / lb);
        if (cos < 0.985) return (0, 0); // more than about 10 degrees apart
        double Proj(Vec2 p) => (p.X - a.Start.X) * ua.X + (p.Y - a.Start.Y) * ua.Y;
        var p0 = Proj(b.Start); var p1 = Proj(b.End);
        var lo = Math.Max(0, Math.Min(p0, p1)); var hi = Math.Min(la, Math.Max(p0, p1));
        if (hi <= lo) return (0, 0);
        var mid = b.Start + db * 0.5;
        var dist = Math.Abs((mid.X - a.Start.X) * -ua.Y + (mid.Y - a.Start.Y) * ua.X);
        return (hi - lo, dist);
    }
}
