using System.Text.Json;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Analysis;

/// <summary>
/// What the firmware (or the designer) expects the board to connect, kept in
/// a "&lt;board&gt;.boardsim.json" file next to the board:
/// <code>
/// { "connections": [ { "from": "U1.GPIO7", "to": "U2.Z", "why": "LED data (RMT)" } ] }
/// </code>
/// A connection may pass through resistors, inductors and parts whose model
/// lists the pins a signal passes through (a buffer's A to Y).
/// </summary>
public sealed class Expectations
{
    public sealed class Connection
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public string? Why { get; set; }
    }

    /// <summary>A signal the firmware puts on a pin, for the noise and emission estimates.</summary>
    public sealed class SignalSpec
    {
        /// <summary>REF.PIN (U1.GPIO7) or a net name.</summary>
        public string Pin { get; set; } = "";
        public double Hz { get; set; }
        /// <summary>Fraction of the period the signal is high (0.5 for a clock).</summary>
        public double Duty { get; set; } = 0.5;
        /// <summary>Overrides the edge time from the driver's part model.</summary>
        public double? RiseNs { get; set; }
        public string? Why { get; set; }
    }

    public string? Description { get; set; }
    /// <summary>Signals for the emission estimate (LED data, SPI clock...).</summary>
    public List<SignalSpec> Signals { get; set; } = new();
    /// <summary>Cable length in metres on a connector (J4: 5), for the common-mode estimate.</summary>
    public Dictionary<string, double> Cables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Byte order of the LED strings on the connectors: "GRB" (WS2812 default), "RGB", "GRBW"...</summary>
    public string? LedOrder { get; set; }
    public List<Connection> Connections { get; set; } = new();
    public string Source { get; private set; } = "";

    public static Expectations Load(string path)
    {
        var e = JsonSerializer.Deserialize<Expectations>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new Expectations();
        e.Source = path;
        return e;
    }

    /// <summary>"board.kicad_pcb" -> "board.boardsim.json", if it exists.</summary>
    public static Expectations? FindFor(string boardPath)
    {
        var candidate = Path.ChangeExtension(boardPath, ".boardsim.json");
        return File.Exists(candidate) ? Load(candidate) : null;
    }
}

public sealed class ExpectationRule : IRule
{
    public string Id => "expected-connection";
    public string Description => "Connections listed in the board's .boardsim.json exist.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        if (ctx.Expectations is null) yield break;
        var c = ctx.Circuit;
        foreach (var e in ctx.Expectations.Connections)
        {
            var from = Resolve(c, e.From);
            var to = Resolve(c, e.To);
            var why = e.Why is { Length: > 0 } w ? $" ({w})" : "";
            if (from is null || to is null)
            {
                yield return new Finding
                {
                    Severity = Severity.Error, Rule = Id,
                    Title = $"Cannot find {(from is null ? e.From : e.To)} on the board",
                    Detail = $"Listed in {Path.GetFileName(ctx.Expectations.Source)}{why}. Use REF.PIN with a pin number or a pin name from the part model.",
                };
                continue;
            }
            var path = Trace(c, from, to);
            if (path is null)
            {
                var reach = Trace(c, from, null) ?? new List<string>();
                yield return new Finding
                {
                    Severity = Severity.Error, Rule = Id,
                    Title = $"{from.Label} does not reach {to.Label}{why}",
                    Detail = $"Expected by {Path.GetFileName(ctx.Expectations.Source)}. {from.Label} goes to: " +
                             (reach.Count == 0 ? "nothing" : string.Join(", ", reach.Distinct().Take(12))) + ".",
                    Refs = new[] { from.Id, to.Id },
                    Nets = new[] { from.Net.Name, to.Net.Name },
                    Location = from.Pad.Position,
                };
            }
        }
    }

    /// <summary>The route from one pin to another, or (with to = null) every pin reached.</summary>
    public static List<string>? Trace(Circuit c, PinRef from, PinRef? to)
    {
        var seenNets = new HashSet<Net>();
        var reached = new List<string>();
        var queue = new Queue<(Net Net, List<string> Path)>();
        queue.Enqueue((from.Net, new List<string> { from.Id }));
        while (queue.Count > 0)
        {
            var (net, path) = queue.Dequeue();
            if (!seenNets.Add(net)) continue;
            foreach (var pin in net.Pins)
            {
                if (pin == from) continue;
                if (to is not null && pin == to) return path.Append(pin.Id).ToList();
                reached.Add(pin.Label);
                foreach (var next in PassThrough(pin))
                    if (!seenNets.Contains(next.Net))
                        queue.Enqueue((next.Net, path.Append(pin.Id).Append(next.Id).ToList()));
            }
        }
        return to is null ? reached : null;
    }

    private static IEnumerable<PinRef> PassThrough(PinRef pin)
    {
        var part = pin.Part;
        if (part.Kind is PartKind.Resistor or PartKind.Inductor && part.IsTwoTerminal)
        {
            if (part.Other(pin.Net) is { IsRail: false } other)
                foreach (var p in part.Pins.Where(p => p.Net == other)) yield return p;
            yield break;
        }
        foreach (var pair in part.Model?.Through ?? new List<string[]>())
        {
            if (pair.Length != 2) continue;
            if (string.Equals(pin.Name, pair[0], StringComparison.OrdinalIgnoreCase))
                foreach (var p in part.PinsNamed(pair[1])) yield return p;
            else if (string.Equals(pin.Name, pair[1], StringComparison.OrdinalIgnoreCase))
                foreach (var p in part.PinsNamed(pair[0])) yield return p;
        }
    }

    public static PinRef? Resolve(Circuit c, string spec)
    {
        var dot = spec.IndexOf('.');
        if (dot <= 0) return null;
        var part = c.Part(spec[..dot]);
        var pin = spec[(dot + 1)..];
        return part?.Pin(pin) ?? part?.PinsNamed(pin).FirstOrDefault();
    }
}
