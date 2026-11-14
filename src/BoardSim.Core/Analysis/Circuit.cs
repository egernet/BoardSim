using System.Globalization;
using System.Text.RegularExpressions;
using BoardSim.Core.Model;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Analysis;

public enum PartKind
{
    Ic, Regulator, Resistor, Capacitor, Inductor, Diode, Led, Tvs, Connector, TestPoint,
    Switch, Crystal, Transistor, Antenna, MountingHole, Fuse, Other,
}

public sealed class Part
{
    public required Footprint Footprint { get; init; }
    public PartModel? Model { get; init; }
    public PartKind Kind { get; init; }
    /// <summary>Ohms, farads or henries for passives; null when the value cannot be read.</summary>
    public double? Value { get; init; }
    public List<PinRef> Pins { get; } = new();

    public string Reference => Footprint.Reference;
    public bool IsTwoTerminal => Pins.Select(p => p.Number).Distinct().Count() == 2;

    /// <summary>The net on the other side of a two-terminal part.</summary>
    public Net? Other(Net net)
    {
        var nets = Pins.Select(p => p.Net).Distinct().ToList();
        return nets.Count == 2 ? (nets[0] == net ? nets[1] : nets[1] == net ? nets[0] : null) : null;
    }

    public PinRef? Pin(string number) => Pins.FirstOrDefault(p => p.Number == number);
    public IEnumerable<PinRef> PinsNamed(string name) =>
        Pins.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Supply voltage of the part, from the power pin its model names.</summary>
    public double? SupplyVolts()
    {
        if (Model?.Supply is { } s)
            foreach (var p in PinsNamed(s)) if (p.Net.Volts is { } v) return v;
        return Pins.Where(p => p.Model?.Type == PinType.Power).Select(p => p.Net.Volts).FirstOrDefault(v => v is > 0);
    }

    public override string ToString() => $"{Reference} {Footprint.Value}";
}

public sealed class PinRef
{
    public required Part Part { get; init; }
    public required Pad Pad { get; init; }
    public required Net Net { get; init; }
    public PinModel? Model { get; init; }

    public string Number => Pad.Number;
    public string Name => Model?.Name is { Length: > 0 } n ? n : Pad.PinFunction is { Length: > 0 } f ? f : NameFromNet() ?? Number;
    public string Id => $"{Part.Reference}.{Number}";
    public string Label => Name == Number ? Id : $"{Part.Reference}.{Name} (pin {Number})";

    private string? NameFromNet()
    {
        // KiCad names stub nets after the pin: "unconnected-(U5-DCD-Pad1)", "Net-(U2-~{E})".
        var m = Regex.Match(Net.Name, $@"\({Regex.Escape(Part.Reference)}-(.+?)(-Pad{Regex.Escape(Number)})?\)$");
        return m.Success ? m.Groups[1].Value.Replace("~{", "").Replace("}", "") : null;
    }

    public override string ToString() => Label;
}

public sealed class Net
{
    public required string Name { get; init; }
    public List<PinRef> Pins { get; } = new();
    public double? Volts { get; set; }
    /// <summary>How the voltage was found, e.g. "net name" or "U3.VOUT".</summary>
    public string? VoltsSource { get; set; }

    public bool IsGround => Volts == 0;
    public bool IsRail => Volts is not null;
    /// <summary>KiCad names nets of pins wired to nothing "unconnected-(...)".</summary>
    public bool IsNoConnect => Name.StartsWith("unconnected-", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => Name.TrimStart('/');

    public override string ToString() => DisplayName;
}

/// <summary>The board as an electrical circuit: parts, pins and nets.</summary>
public sealed class Circuit
{
    public Board Board { get; }
    public List<Part> Parts { get; } = new();
    public Dictionary<string, Net> Nets { get; } = new();

    private Circuit(Board board) => Board = board;

    public Part? Part(string reference) =>
        Parts.FirstOrDefault(p => string.Equals(p.Reference, reference, StringComparison.OrdinalIgnoreCase));

    public static Circuit Build(Board board, PartLibrary library)
    {
        var c = new Circuit(board);
        foreach (var fp in board.Footprints.Where(f => !f.DoNotPopulate))
        {
            var model = library.Find(fp);
            var part = new Part { Footprint = fp, Model = model, Kind = Classify(fp, model), Value = Units.Parse(fp.Value) };
            c.Parts.Add(part);
            foreach (var pad in fp.Pads.Where(p => p.HasNet && p.Number.Length > 0 && p.Kind != PadKind.NpThroughHole))
            {
                // Several pads with one number (exposed pad, shield legs) are one pin.
                if (part.Pins.Any(p => p.Number == pad.Number && p.Net.Name == pad.Net)) continue;
                if (!c.Nets.TryGetValue(pad.Net, out var net)) c.Nets[pad.Net] = net = new Net { Name = pad.Net };
                var pin = new PinRef { Part = part, Pad = pad, Net = net, Model = model?.Pin(pad.Number) };
                part.Pins.Add(pin);
                net.Pins.Add(pin);
            }
        }
        c.InferVoltages();
        return c;
    }

    private static PartKind Classify(Footprint fp, PartModel? model)
    {
        if (model is not null)
            return model.Kind switch
            {
                "regulator" => PartKind.Regulator,
                "connector" => PartKind.Connector,
                "tvs" => PartKind.Tvs,
                _ => PartKind.Ic,
            };
        var lib = fp.LibId;
        var value = fp.Value;
        return fp.Prefix.ToUpperInvariant() switch
        {
            "R" or "RN" => PartKind.Resistor,
            "C" => PartKind.Capacitor,
            "L" or "FB" => PartKind.Inductor,
            "D" or "LED" when lib.Contains("LED", StringComparison.OrdinalIgnoreCase) || value.Contains("LED", StringComparison.OrdinalIgnoreCase) => PartKind.Led,
            "D" when Regex.IsMatch(value, "^(SMBJ|SMAJ|SMF|PESD|SP05|ESD|TVS|SRV|USBLC)", RegexOptions.IgnoreCase) => PartKind.Tvs,
            "D" => PartKind.Diode,
            "J" or "P" or "CN" or "CON" or "X" when !lib.Contains("Crystal") => PartKind.Connector,
            "V_IN" or "VIN" or "PWR" => PartKind.Connector,
            "TP" => PartKind.TestPoint,
            "SW" or "S" or "BTN" => PartKind.Switch,
            "Y" or "X" => PartKind.Crystal,
            "Q" or "T" => PartKind.Transistor,
            "AE" or "ANT" => PartKind.Antenna,
            "H" or "MH" => PartKind.MountingHole,
            "F" => PartKind.Fuse,
            "U" or "IC" => PartKind.Ic,
            _ when lib.StartsWith("Connector") || lib.StartsWith("TerminalBlock") => PartKind.Connector,
            _ => PartKind.Other,
        };
    }

    private void InferVoltages()
    {
        foreach (var net in Nets.Values)
            if (Units.VoltsFromNetName(net.DisplayName) is { } v) { net.Volts = v; net.VoltsSource = "net name"; }

        foreach (var pin in Parts.SelectMany(p => p.Pins))
        {
            if (pin.Model?.Type == PinType.PowerOut && pin.Model.Volts is { } v && pin.Net.Volts is null)
            { pin.Net.Volts = v; pin.Net.VoltsSource = pin.Label; }
            if (pin.Model?.Type == PinType.Ground && pin.Net.Volts is null && !pin.Net.IsNoConnect)
            { pin.Net.Volts = 0; pin.Net.VoltsSource = pin.Label; }
        }

        // DC passes through inductors, ferrites, fuses and 0 Ω links.
        bool changed;
        do
        {
            changed = false;
            foreach (var p in Parts.Where(p => p.IsTwoTerminal && IsDcLink(p)))
            {
                var nets = p.Pins.Select(x => x.Net).Distinct().ToList();
                if (nets.Count != 2) continue;
                foreach (var (a, b) in new[] { (nets[0], nets[1]), (nets[1], nets[0]) })
                    if (a.Volts is { } v && b.Volts is null) { b.Volts = v; b.VoltsSource = $"{a.DisplayName} through {p.Reference}"; changed = true; }
            }
        } while (changed);
    }

    private static bool IsDcLink(Part p) =>
        p.Kind is PartKind.Inductor or PartKind.Fuse ||
        (p.Kind == PartKind.Resistor && p.Value is <= 1);

    /// <summary>Resistors from a net to a known rail: the pulls on that net.</summary>
    public IEnumerable<(Part Resistor, Net Rail)> Pulls(Net net) =>
        Parts.Where(p => p.Kind == PartKind.Resistor && p.IsTwoTerminal && p.Pins.Any(x => x.Net == net))
             .Select(p => (p, p.Other(net)))
             .Where(x => x.Item2 is { IsRail: true })
             .Select(x => (x.p, x.Item2!));

    /// <summary>Total capacitance from a net straight to ground.</summary>
    public double CapacitanceToGround(Net net, out List<Part> caps)
    {
        caps = Parts.Where(p => p.Kind == PartKind.Capacitor && p.IsTwoTerminal &&
                                p.Pins.Any(x => x.Net == net) && p.Other(net) is { IsGround: true }).ToList();
        return caps.Sum(c => c.Value ?? 0);
    }

    /// <summary>
    /// Like <see cref="SeriesNets"/>, with the route to each net: every step is
    /// a net and the resistance crossed to reach it from the previous one.
    /// </summary>
    public List<(Net Net, IReadOnlyList<(Net Net, double SeriesOhms)> Path)> SeriesPaths(Net start, int maxDepth = 3)
    {
        var result = new List<(Net, IReadOnlyList<(Net, double)>)> { (start, new[] { (start, 0.0) }) };
        var frontier = new List<(Net Net, List<(Net, double)> Path)> { (start, new List<(Net, double)> { (start, 0.0) }) };
        var seen = new HashSet<Net> { start };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<(Net, List<(Net, double)>)>();
            foreach (var (n, path) in frontier)
                foreach (var r in Parts.Where(p => p.Kind is PartKind.Resistor or PartKind.Inductor && p.IsTwoTerminal && p.Pins.Any(x => x.Net == n)))
                    if (r.Other(n) is { IsRail: false, IsNoConnect: false } o && seen.Add(o))
                    {
                        var p = new List<(Net, double)>(path) { (o, r.Kind == PartKind.Resistor ? r.Value ?? 0 : 0) };
                        result.Add((o, p));
                        next.Add((o, p));
                    }
            frontier = next;
        }
        return result;
    }

    /// <summary>
    /// Nets joined to <paramref name="net"/> through series resistors (not
    /// through pulls to a rail), the net itself included.
    /// </summary>
    public List<Net> SeriesNets(Net net, int maxDepth = 3)
    {
        var seen = new List<Net> { net };
        var frontier = new List<Net> { net };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<Net>();
            foreach (var n in frontier)
                foreach (var r in Parts.Where(p => p.Kind is PartKind.Resistor or PartKind.Inductor && p.IsTwoTerminal && p.Pins.Any(x => x.Net == n)))
                    if (r.Other(n) is { IsRail: false, IsNoConnect: false } o && !seen.Contains(o)) { seen.Add(o); next.Add(o); }
            frontier = next;
        }
        return seen;
    }
}

public static class Units
{
    private static readonly Regex ValueRx = new(@"^\s*(\d+(?:[.,]\d+)?)\s*([pnuµμmkKMGR]?)(\d*)", RegexOptions.Compiled);

    /// <summary>Reads "10K", "4k7", "100nF", "1uf/16V", "2.0nH", "510" into a number in base units.</summary>
    public static double? Parse(string text)
    {
        var m = ValueRx.Match(text);
        if (!m.Success) return null;
        var number = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        var mult = m.Groups[2].Value switch
        {
            "p" => 1e-12, "n" => 1e-9, "u" or "µ" or "μ" => 1e-6, "m" => 1e-3,
            "k" or "K" => 1e3, "M" => 1e6, "G" => 1e9, _ => 1,
        };
        if (m.Groups[3].Value.Length > 0 && m.Groups[2].Value.Length > 0)
            number += double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture); // 4k7
        return number * mult;
    }

    /// <summary>Voltage implied by a net name: GND, +5V, 3V3, +3.3V, VBUS, VCC_5V...</summary>
    public static double? VoltsFromNetName(string name)
    {
        var n = name.Split('/').Last().ToUpperInvariant();
        if (Regex.IsMatch(n, @"^(GND|AGND|DGND|PGND|GNDA|GNDD|GNDPWR|VSS|0V|EARTH|CHASSIS)\d*$")) return 0;
        if (n is "VBUS" or "USB_VBUS" or "VUSB") return 5;
        // Only supply-like names: "3V3", "+5V", "+3.3V", or a supply prefix such as
        // VCC_3V3 / VDD_5V. "LED_3V3" is a signal next to a rail, not the rail.
        var m = Regex.Match(n, @"^(?:(?:VCC|VDD|VDDA|VIN|VSYS|VBAT|PWR|P|V)_?)?\+?(\d+)V(\d+)$");      // 3V3, VCC_3V3
        if (m.Success) return double.Parse($"{m.Groups[1].Value}.{m.Groups[2].Value}", CultureInfo.InvariantCulture);
        m = Regex.Match(n, @"^(?:(?:VCC|VDD|VDDA|VIN|VSYS|VBAT|PWR|P|V)_?)?\+?(\d+(?:\.\d+)?)V$");     // +5V, +3.3V, VCC_5V
        if (m.Success) return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    public static string Format(double value, string unit)
    {
        var abs = Math.Abs(value);
        var (div, prefix) = abs switch
        {
            >= 1e6 => (1e6, "M"), >= 1e3 => (1e3, "k"), >= 1 => (1, ""), >= 1e-3 => (1e-3, "m"),
            >= 1e-6 => (1e-6, "µ"), >= 1e-9 => (1e-9, "n"), _ => (1e-12, "p"),
        };
        return (value / div).ToString("0.##", CultureInfo.InvariantCulture) + " " + prefix + unit;
    }
}
