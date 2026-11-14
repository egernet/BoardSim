using BoardSim.Core.Analysis;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Simulation;

/// <summary>How a pin is driven from outside: by the user, or later by an emulated MCU.</summary>
public enum PinDrive { Input, High, Low }

public enum NetState { Floating, Low, Mid, High, Conflict }

public sealed record NetResult(double? Volts, NetState State, string? Note = null);

/// <summary>
/// DC simulation of a board. Every net is a node; resistors, inductors, closed
/// switches, LEDs and analog switches are conductances; rails and driven
/// outputs are sources with a small output resistance. Digital parts read
/// their inputs from the solved voltages and set their outputs, and the
/// circuit is solved again until nothing changes.
/// </summary>
public sealed class Simulator
{
    public Circuit Circuit { get; }
    private readonly List<IPartBehavior> _behaviors = new();
    private readonly Dictionary<PinRef, (PinDrive Drive, double Volts)> _pinDrives = new();
    private readonly HashSet<Part> _pressed = new();

    public IReadOnlyDictionary<Net, NetResult> Nets { get; private set; } = new Dictionary<Net, NetResult>();
    /// <summary>Forward current through each LED in amps (0 when off).</summary>
    public IReadOnlyDictionary<Part, double> LedCurrent { get; private set; } = new Dictionary<Part, double>();
    public int Iterations { get; private set; }

    public Simulator(Circuit circuit)
    {
        Circuit = circuit;
        foreach (var part in circuit.Parts)
            if (BehaviorFactory.Create(part) is { } b) _behaviors.Add(b);
    }

    /// <summary>Pins the user can drive: MCU I/O and signal pins on connectors.</summary>
    public IEnumerable<PinRef> ControllablePins =>
        Circuit.Parts.Where(p => p.Model?.Behavior?.Type == "mcu" || p.Kind == PartKind.Connector)
            .SelectMany(p => p.Pins)
            .Where(p => !p.Net.IsRail && !p.Net.IsNoConnect && (p.Part.Kind == PartKind.Connector || p.Model?.Type is PinType.Io or PinType.In or PinType.Out))
            .Distinct();

    public IEnumerable<Part> Switches => Circuit.Parts.Where(p => p.Kind == PartKind.Switch);

    public PinDrive DriveOf(PinRef pin) => _pinDrives.TryGetValue(pin, out var d) ? d.Drive : PinDrive.Input;

    /// <summary>Drives a pin. High uses the part's logic supply unless <paramref name="volts"/> is given.</summary>
    public void SetPin(PinRef pin, PinDrive drive, double? volts = null)
    {
        if (drive == PinDrive.Input) { _pinDrives.Remove(pin); return; }
        var high = volts ?? pin.Part.SupplyVolts() ?? 3.3;
        _pinDrives[pin] = (drive, drive == PinDrive.High ? high : 0);
    }

    private readonly Dictionary<Part, double> _crystals = new();

    /// <summary>A crystal's frequency: the value on the board, unless the user changed it.</summary>
    public double? CrystalHz(Part crystal) => _crystals.TryGetValue(crystal, out var hz) ? hz : crystal.Value;
    public void SetCrystalHz(Part crystal, double? hz) { if (hz is { } v) _crystals[crystal] = v; else _crystals.Remove(crystal); }

    /// <summary>
    /// How fast a chip's clock runs compared to what it was built for: 1 with the
    /// right crystal, 0.65 with a 26 MHz crystal on a 40 MHz part.
    /// </summary>
    public double ClockRatio(Part chip)
    {
        if (chip.Model?.CrystalHz is not { } expected) return 1;
        if (Circuit.CrystalOf(chip) is not { } crystal || CrystalHz(crystal) is not { } hz) return 1;
        return hz / expected;
    }

    public bool IsPressed(Part sw) => _pressed.Contains(sw);
    public void SetPressed(Part sw, bool pressed) { if (pressed) _pressed.Add(sw); else _pressed.Remove(sw); }

    public void Run() => Solve(publish: true);

    /// <summary>
    /// Solves the circuit. With <paramref name="publish"/> false the result is
    /// only returned: <see cref="Nets"/> keeps what the user sees (used for
    /// what-if solves such as tracing a signal).
    /// </summary>
    public IReadOnlyDictionary<Net, NetResult> Solve(bool publish)
    {
        var nets = Circuit.Nets.Values.ToList();
        var index = nets.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);
        var leds = Circuit.Parts.Where(p => p.Kind is PartKind.Led or PartKind.Diode && DiodePins(p) is not null).ToList();
        var ledOn = leds.ToDictionary(l => l, _ => false);
        double[] volts = new double[nets.Count];

        foreach (var b in _behaviors) b.Reset();

        for (Iterations = 1; Iterations <= 60; Iterations++)
        {
            var sys = new NodalSystem(nets.Count);
            var sources = new List<(int Node, double Volts, double G, string Who, bool Strong)>();

            // Rails.
            foreach (var n in nets.Where(n => n.Volts is not null))
                sources.Add((index[n], n.Volts!.Value, 1e4, n.DisplayName, true));

            // Passive parts.
            foreach (var p in Circuit.Parts.Where(p => p.IsTwoTerminal))
            {
                var pins = p.Pins.Select(x => x.Net).Distinct().ToList();
                if (pins.Count != 2) continue;
                var (a, b) = (index[pins[0]], index[pins[1]]);
                switch (p.Kind)
                {
                    case PartKind.Resistor when p.Value is > 0:
                        sys.Conductance(a, b, 1 / Math.Max(p.Value.Value, 1e-3));
                        break;
                    case PartKind.Inductor or PartKind.Fuse:
                        sys.Conductance(a, b, 10);
                        break;
                }
            }

            // Switches: pins with one number are one terminal; pressed joins the two.
            foreach (var sw in Circuit.Parts.Where(p => p.Kind == PartKind.Switch && _pressed.Contains(p)))
            {
                var nn = sw.Pins.Select(x => x.Net).Distinct().ToList();
                for (var i = 1; i < nn.Count; i++) sys.Conductance(index[nn[0]], index[nn[i]], 10);
            }

            // Diodes and LEDs: off = open, on = forward voltage plus a small resistance.
            foreach (var d in leds.Where(d => ledOn[d]))
            {
                var (an, kn) = DiodePins(d)!.Value;
                var (vf, rs) = DiodeModel(d);
                sys.DiodeOn(index[an], index[kn], vf, 1 / rs);
            }

            // User-driven pins.
            foreach (var (pin, (drive, v)) in _pinDrives)
                sources.Add((index[pin.Net], v, 1 / 30.0, pin.Label, true));

            // Part behaviours (mux switches, buffer outputs, MCU pulls).
            var ctx = new StampContext(sys, index, sources, volts, this);
            foreach (var b in _behaviors) b.Stamp(ctx);

            foreach (var s in sources) sys.Source(s.Node, s.Volts, s.G);

            var reachable = sys.ConnectedToSource(sources.Select(s => s.Node));
            var solved = sys.Solve();
            for (var i = 0; i < volts.Length; i++) volts[i] = reachable[i] ? solved[i] : double.NaN;

            // Update diodes.
            var changed = false;
            foreach (var d in leds)
            {
                var (an, kn) = DiodePins(d)!.Value;
                var va = volts[index[an]];
                var vk = volts[index[kn]];
                var (vf, rs) = DiodeModel(d);
                bool on;
                if (double.IsNaN(va) || double.IsNaN(vk)) on = false;
                else if (ledOn[d]) on = (va - vk - vf) / rs > -1e-6;
                else on = va - vk > vf + 1e-4;
                if (on != ledOn[d]) { ledOn[d] = on; changed = true; }
            }

            // Let digital parts react to the new voltages.
            foreach (var b in _behaviors)
                if (b.Update(new ReadContext(index, volts, Circuit))) changed = true;

            if (!changed)
            {
                return Publish(nets, index, volts, sources, leds, ledOn, publish);
            }
        }
        return Publish(nets, index, volts, new(), leds, ledOn, publish);
    }

    private IReadOnlyDictionary<Net, NetResult> Publish(List<Net> nets, Dictionary<Net, int> index, double[] volts,
        List<(int Node, double Volts, double G, string Who, bool Strong)> sources, List<Part> leds, Dictionary<Part, bool> ledOn, bool publish)
    {
        var result = new Dictionary<Net, NetResult>();
        foreach (var n in nets)
        {
            var v = volts[index[n]];
            if (double.IsNaN(v)) { result[n] = new NetResult(null, NetState.Floating); continue; }

            // Strong sources on one node that disagree: a short or two outputs fighting.
            var strong = sources.Where(s => s.Node == index[n] && s.Strong).ToList();
            var fight = strong.Where(s => Math.Abs(s.Volts - v) * s.G > 0.025).ToList();
            if (fight.Count > 0 && strong.Select(s => Math.Round(s.Volts, 2)).Distinct().Count() > 1)
            {
                result[n] = new NetResult(v, NetState.Conflict,
                    "Driven both ways: " + string.Join(", ", strong.Select(s => $"{s.Who} ({s.Volts:0.##} V)")));
                continue;
            }
            var state = v < 0.8 ? NetState.Low : v > 2.0 ? NetState.High : NetState.Mid;
            result[n] = new NetResult(v, state);
        }
        if (!publish) return result;
        Nets = result;

        var currents = new Dictionary<Part, double>();
        foreach (var d in leds.Where(d => d.Kind == PartKind.Led))
        {
            if (!ledOn[d]) { currents[d] = 0; continue; }
            var (an, kn) = DiodePins(d)!.Value;
            var va = volts[index[an]];
            var vk = volts[index[kn]];
            var (vf, rs) = DiodeModel(d);
            currents[d] = Math.Max(0, (va - vk - vf) / rs);
        }
        LedCurrent = currents;
        return result;
    }

    /// <summary>
    /// Anode and cathode nets. KiCad's diode and LED footprints number the
    /// cathode 1 and the anode 2; some libraries name the pads A and K.
    /// </summary>
    public static (Net Anode, Net Cathode)? DiodePins(Part d)
    {
        var a = d.Pin("A") ?? d.PinsNamed("A").FirstOrDefault() ?? d.Pin("2");
        var k = d.Pin("K") ?? d.PinsNamed("K").FirstOrDefault() ?? d.Pin("1");
        return a is null || k is null || a.Net == k.Net ? null : (a.Net, k.Net);
    }

    /// <summary>Forward voltage and series resistance by LED colour (or a small-signal diode).</summary>
    public static (double Vf, double Rs) DiodeModel(Part d)
    {
        var v = d.Footprint.Value.ToUpperInvariant();
        if (d.Kind == PartKind.Diode) return (v.StartsWith("SS") || v.Contains("5819") || v.StartsWith("B5") ? 0.35 : 0.65, 0.5);
        if (v.Contains("RED")) return (1.9, 15);
        if (v.Contains("YELLOW") || v.Contains("ORANGE")) return (2.0, 15);
        if (v.Contains("GREEN")) return (2.1, 15);
        return (2.9, 15); // blue, white
    }

    public sealed class StampContext(NodalSystem sys, Dictionary<Net, int> index,
        List<(int Node, double Volts, double G, string Who, bool Strong)> sources, double[] volts, Simulator sim)
    {
        public Simulator Simulator { get; } = sim;
        public double Volts(Net n) => volts[index[n]];
        public void Conductance(Net a, Net b, double g) => sys.Conductance(index[a], index[b], g);
        public void Drive(Net n, double v, double g, string who, bool strong = true) => sources.Add((index[n], v, g, who, strong));
    }

    public sealed class ReadContext(Dictionary<Net, int> index, double[] volts, Circuit circuit)
    {
        public Circuit Circuit { get; } = circuit;
        public double Volts(Net n) => volts[index[n]];
    }
}

/// <summary>Modified nodal analysis with every source referenced to ground.</summary>
public sealed class NodalSystem
{
    private readonly int _n;
    private readonly double[,] _g;
    private readonly double[] _i;
    private readonly List<(int A, int B)> _links = new();

    public NodalSystem(int n)
    {
        _n = n;
        _g = new double[n, n];
        _i = new double[n];
    }

    public void Conductance(int a, int b, double g)
    {
        if (a == b) return;
        _g[a, a] += g; _g[b, b] += g;
        _g[a, b] -= g; _g[b, a] -= g;
        _links.Add((a, b));
    }

    /// <summary>A voltage source to ground with output conductance <paramref name="g"/>.</summary>
    public void Source(int node, double volts, double g)
    {
        _g[node, node] += g;
        _i[node] += volts * g;
    }

    /// <summary>A conducting diode: current = g (Va - Vk - Vf).</summary>
    public void DiodeOn(int anode, int cathode, double vf, double g)
    {
        Conductance(anode, cathode, g);
        _i[anode] += g * vf;
        _i[cathode] -= g * vf;
    }

    public bool[] ConnectedToSource(IEnumerable<int> sourceNodes)
    {
        var adj = new List<int>[_n];
        for (var i = 0; i < _n; i++) adj[i] = new List<int>();
        foreach (var (a, b) in _links) { adj[a].Add(b); adj[b].Add(a); }
        var seen = new bool[_n];
        var stack = new Stack<int>(sourceNodes);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (seen[x]) continue;
            seen[x] = true;
            foreach (var y in adj[x]) if (!seen[y]) stack.Push(y);
        }
        return seen;
    }

    public double[] Solve()
    {
        // A tiny leak to ground keeps nodes that nothing drives from making the matrix singular.
        var a = (double[,])_g.Clone();
        var b = (double[])_i.Clone();
        for (var i = 0; i < _n; i++) a[i, i] += 1e-12;

        for (var col = 0; col < _n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < _n; r++) if (Math.Abs(a[r, col]) > Math.Abs(a[pivot, col])) pivot = r;
            if (pivot != col)
            {
                for (var c = 0; c < _n; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                (b[col], b[pivot]) = (b[pivot], b[col]);
            }
            var d = a[col, col];
            if (Math.Abs(d) < 1e-300) continue;
            for (var r = col + 1; r < _n; r++)
            {
                var f = a[r, col] / d;
                if (f == 0) continue;
                for (var c = col; c < _n; c++) a[r, c] -= f * a[col, c];
                b[r] -= f * b[col];
            }
        }
        var x = new double[_n];
        for (var r = _n - 1; r >= 0; r--)
        {
            var s = b[r];
            for (var c = r + 1; c < _n; c++) s -= a[r, c] * x[c];
            x[r] = Math.Abs(a[r, r]) < 1e-300 ? 0 : s / a[r, r];
        }
        return x;
    }
}
