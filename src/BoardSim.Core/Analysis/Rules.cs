using System.Globalization;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Analysis;

public sealed class CheckContext
{
    public required Circuit Circuit { get; init; }
    public Connectivity? Connectivity { get; init; }
    public Expectations? Expectations { get; init; }
}

public interface IRule
{
    string Id { get; }
    string Description { get; }
    IEnumerable<Finding> Check(CheckContext ctx);
}

/// <summary>Helpers shared by the rules.</summary>
internal static class RuleHelpers
{
    public static string V(double v) => v.ToString("0.##", CultureInfo.InvariantCulture) + " V";

    public static Finding Make(Severity s, string rule, string title, string detail, PinRef? pin = null,
        IEnumerable<string>? refs = null, IEnumerable<string>? nets = null) => new()
    {
        Severity = s,
        Rule = rule,
        Title = title,
        Detail = detail,
        Refs = (pin is null ? Enumerable.Empty<string>() : new[] { pin.Id }).Concat(refs ?? Enumerable.Empty<string>()).Distinct().ToList(),
        Nets = (pin is null ? Enumerable.Empty<string>() : new[] { pin.Net.Name }).Concat(nets ?? Enumerable.Empty<string>()).Distinct().ToList(),
        Location = pin?.Pad.Position,
    };

    public static string Why(PinRef pin) => pin.Model?.Why is { Length: > 0 } w ? " " + w : "";

    /// <summary>Voltage an input threshold resolves to: "2.0" or "0.7*VCC".</summary>
    public static double? Threshold(string? vih, Part part)
    {
        if (vih is null) return null;
        var parts = vih.Split('*');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var k)) return null;
        if (parts.Length == 1) return k;
        return part.SupplyVolts() is { } s ? k * s : null;
    }

    /// <summary>
    /// Pins that drive a net: outputs and I/O of other parts, also through series
    /// resistors. <c>Ratio</c> is how much of the driver's voltage reaches the
    /// target when pull-downs along the way form a divider (1 = all of it).
    /// </summary>
    public static IEnumerable<(PinRef Driver, bool ThroughResistor, double Ratio)> Drivers(Circuit c, PinRef target)
    {
        foreach (var (net, path) in c.SeriesPaths(target.Net))
            foreach (var p in net.Pins)
                if (p.Part != target.Part && p.Model?.Type is PinType.Out or PinType.Io)
                    yield return (p, net != target.Net, DividerRatio(c, path));
    }

    /// <summary>
    /// Walks from the driver's net back to the target: series resistance adds
    /// up, and a pull-down on a net divides the voltage there.
    /// </summary>
    private static double DividerRatio(Circuit c, IReadOnlyList<(Net Net, double SeriesOhms)> path)
    {
        var ratio = 1.0;
        var series = 0.0;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var (net, ohms) = path[i];
            if (i < path.Count - 1)
            {
                var downs = c.Pulls(net).Where(x => x.Rail.IsGround && x.Resistor.Value is > 0).Select(x => x.Resistor.Value!.Value).ToList();
                if (downs.Count > 0 && series > 0)
                {
                    var rd = 1 / downs.Sum(r => 1 / r);
                    ratio *= rd / (series + rd);
                    series = 0;
                }
            }
            series += ohms;
        }
        return ratio;
    }
}

/// <summary>Pads of one net that the copper does not join.</summary>
public sealed class UnroutedRule : IRule
{
    public string Id => "unrouted";
    public string Description => "Every net must be joined by copper: tracks, vias or zones.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        if (ctx.Connectivity is null) yield break;
        foreach (var (net, islands) in ctx.Connectivity.Unrouted)
        {
            var biggest = islands.OrderByDescending(i => i.Pads.Count).First();
            var loose = islands.Where(i => i != biggest).SelectMany(i => i.Pads).ToList();
            yield return new Finding
            {
                Severity = Severity.Error,
                Rule = Id,
                Title = $"{net.TrimStart('/')} is not connected to {string.Join(", ", loose.Select(p => p.Id))}",
                Detail = $"The net is split into {islands.Count} copper islands: " +
                         string.Join(" | ", islands.Select(i => string.Join(", ", i.Pads.Select(p => p.Id)))) +
                         ". The schematic joins them, but no track, via or zone does.",
                Refs = loose.Select(p => p.Id).ToList(),
                Nets = new[] { net },
                Location = loose.First().Position,
            };
        }
    }
}

/// <summary>Power and ground pins on the wrong rail, or on none.</summary>
public sealed class PowerPinRule : IRule
{
    public string Id => "power-pin";
    public string Description => "Power pins sit on a rail within their range; ground pins on ground.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        foreach (var pin in ctx.Circuit.Parts.SelectMany(p => p.Pins).Where(p => p.Model is not null))
        {
            var m = pin.Model!;
            if (m.Type == PinType.Ground && !pin.Net.IsGround)
            {
                yield return RuleHelpers.Make(Severity.Error, Id, $"{pin.Label} should be on ground but is on {pin.Net.DisplayName}",
                    pin.Net.Volts is { } v ? $"{pin.Net.DisplayName} is at {RuleHelpers.V(v)}." : "The net is not a ground net.", pin);
            }
            else if (m.Type == PinType.Power)
            {
                if (pin.Net.IsNoConnect)
                {
                    yield return RuleHelpers.Make(Severity.Error, Id, $"{pin.Label} is not connected", "A power pin must be on a supply." + RuleHelpers.Why(pin), pin);
                    continue;
                }
                if (pin.Net.Volts is not { } v) continue;
                var (lo, hi) = m.Range is { Length: 2 } r ? (r[0], r[1]) : m.Volts is { } nom ? (nom * 0.9, nom * 1.1) : (double.MinValue, double.MaxValue);
                if (v < lo || v > hi)
                    yield return RuleHelpers.Make(v > hi ? Severity.Error : Severity.Warning, Id,
                        $"{pin.Label} gets {RuleHelpers.V(v)}, allowed {RuleHelpers.V(lo)} to {RuleHelpers.V(hi)}",
                        $"{pin.Net.DisplayName} is {RuleHelpers.V(v)} ({pin.Net.VoltsSource}).", pin);
            }
        }
    }
}

/// <summary>Inputs left floating and pins that must stay unconnected.</summary>
public sealed class FloatingPinRule : IRule
{
    public string Id => "floating-pin";
    public string Description => "CMOS inputs are driven or pulled; no-connect pins stay unconnected.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.SelectMany(p => p.Pins).Where(p => p.Model is not null))
        {
            var m = pin.Model!;
            if (m.Type == PinType.Nc && !pin.Net.IsNoConnect && pin.Net.Pins.Count > 1)
            {
                yield return RuleHelpers.Make(Severity.Warning, Id, $"{pin.Label} should not be connected",
                    $"It is wired to {string.Join(", ", pin.Net.Pins.Where(p => p != pin).Select(p => p.Id))}." + RuleHelpers.Why(pin), pin);
                continue;
            }
            if (m.Type != PinType.In || m.InternalPull is not null) continue;
            var others = pin.Net.Pins.Where(p => p != pin).ToList();
            var driven = pin.Net.IsRail || others.Any(p => p.Model?.Type is PinType.Out or PinType.Io or PinType.PowerOut)
                         || c.Pulls(pin.Net).Any() || others.Any(p => p.Part.Kind is PartKind.Connector);
            if (pin.Net.IsNoConnect || !driven)
                yield return RuleHelpers.Make(Severity.Error, Id, $"{pin.Label} is a floating input",
                    "Nothing drives or pulls this input, so it reads random levels and can draw extra current." + RuleHelpers.Why(pin), pin);
        }
    }
}

/// <summary>Pins that need a pull resistor (enable lines, USB-C CC...).</summary>
public sealed class PullRule : IRule
{
    public string Id => "pull";
    public string Description => "Pins whose model asks for a pull-up or pull-down have one.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        foreach (var pin in ctx.Circuit.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.Pull is not null))
        {
            var up = pin.Model!.Pull == "up";
            var pulls = ctx.Circuit.Pulls(pin.Net).Where(x => up ? x.Rail.Volts > 0 : x.Rail.IsGround).ToList();
            if (pulls.Count == 0)
                yield return RuleHelpers.Make(Severity.Warning, Id, $"{pin.Label} has no pull-{pin.Model.Pull}",
                    $"No resistor goes from {pin.Net.DisplayName} to {(up ? "a supply" : "ground")}." + RuleHelpers.Why(pin), pin);
        }
    }
}

/// <summary>Strapping pins must read their boot level at reset.</summary>
public sealed class StrapPinRule : IRule
{
    public string Id => "strap-pin";
    public string Description => "Strapping pins are pulled to their boot level and not loaded the other way.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.Strap is not null))
        {
            var high = pin.Model!.Strap == "high";
            var pulls = c.Pulls(pin.Net).ToList();
            var toLevel = pulls.Where(x => high ? x.Rail.Volts > 0 : x.Rail.IsGround).ToList();
            var against = pulls.Where(x => high ? x.Rail.IsGround : x.Rail.Volts > 0).ToList();

            // An LED that conducts from the pin to ground (high strap) or from a rail into the pin (low strap).
            var leds = LedLoads(c, pin.Net, high).ToList();
            foreach (var (led, path) in leds)
                yield return RuleHelpers.Make(Severity.Error, Id,
                    $"{pin.Label} is loaded by {led.Reference}, which pulls it {(high ? "low" : "high")} at boot",
                    $"{pin.Label} must read {pin.Model.Strap} at reset, but {path} conducts from it. " +
                    $"With an LED the pin settles near the LED's forward voltage instead." + RuleHelpers.Why(pin),
                    pin, refs: new[] { led.Reference }.Concat(path.Split(" → ").Where(s => s != led.Reference && c.Part(s) is not null)));

            foreach (var (r, rail) in against)
            {
                var weaker = toLevel.All(t => (t.Resistor.Value ?? double.MaxValue) > (r.Value ?? 0));
                if (weaker)
                    yield return RuleHelpers.Make(Severity.Error, Id,
                        $"{pin.Label} is pulled {(high ? "down" : "up")} by {r.Reference}, but must read {pin.Model.Strap} at reset",
                        $"{r.Reference} ({Units.Format(r.Value ?? 0, "Ω")}) goes to {rail.DisplayName}." + RuleHelpers.Why(pin), pin, refs: new[] { r.Reference });
            }

            var internalOk = pin.Model.InternalPull == (high ? "up" : "down");
            if (toLevel.Count == 0 && !internalOk && leds.Count == 0 && against.Count == 0)
                yield return RuleHelpers.Make(Severity.Warning, Id, $"{pin.Label} has no pull-{(high ? "up" : "down")}",
                    $"It is a strapping pin and must read {pin.Model.Strap} at reset. Without a pull it depends on whatever else is on the net." + RuleHelpers.Why(pin), pin);
        }
    }

    private static IEnumerable<(Part Led, string Path)> LedLoads(Circuit c, Net net, bool towardGround)
    {
        // pin → LED → ground, or pin → R → LED → ground, or pin → LED → R → ground.
        foreach (var start in c.SeriesNets(net, 1))
        {
            var viaR = start == net ? null : c.Parts.First(p => p.Kind == PartKind.Resistor && p.Pins.Any(x => x.Net == net) && p.Pins.Any(x => x.Net == start));
            foreach (var led in c.Parts.Where(p => p.Kind == PartKind.Led && p.IsTwoTerminal))
            {
                // KiCad LED footprints: pad 1 = cathode, pad 2 = anode.
                var anode = led.Pin("2")?.Net;
                var cathode = led.Pin("1")?.Net;
                if (anode is null || cathode is null) continue;
                var (near, far) = towardGround ? (anode, cathode) : (cathode, anode);
                if (near != start) continue;
                string Prefix() => viaR is null ? "" : viaR.Reference + " → ";
                if (towardGround ? far.IsGround : far.Volts > 0)
                    yield return (led, Prefix() + led.Reference + " → " + far.DisplayName);
                else
                    foreach (var (r, rail) in c.Pulls(far).Where(x => towardGround ? x.Rail.IsGround : x.Rail.Volts > 0))
                        yield return (led, Prefix() + led.Reference + " → " + r.Reference + " → " + rail.DisplayName);
            }
        }
    }
}

/// <summary>Capacitance limits from the models (regulator stability, RC delays, strapping pins).</summary>
public sealed class CapacitanceRule : IRule
{
    public string Id => "capacitance";
    public string Description => "Capacitance from a pin's net to ground is within the model's limits.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        foreach (var pin in ctx.Circuit.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.MinCap is not null || p.Model?.MaxCap is not null))
        {
            var total = ctx.Circuit.CapacitanceToGround(pin.Net, out var caps);
            var list = caps.Count == 0 ? "none" : string.Join(", ", caps.Select(x => $"{x.Reference} {x.Footprint.Value}"));
            if (pin.Model!.MinCap is { } min && total < min * 0.99)
                yield return RuleHelpers.Make(Severity.Warning, Id,
                    $"{pin.Label} has {Units.Format(total, "F")} to ground, needs at least {Units.Format(min, "F")}",
                    $"Capacitors on {pin.Net.DisplayName}: {list}." + RuleHelpers.Why(pin), pin, refs: caps.Select(x => x.Reference));
            if (pin.Model.MaxCap is { } max && total > max * 1.01)
                yield return RuleHelpers.Make(Severity.Warning, Id,
                    $"{pin.Label} has {Units.Format(total, "F")} to ground, at most {Units.Format(max, "F")} is advised",
                    $"Capacitors on {pin.Net.DisplayName}: {list}." + RuleHelpers.Why(pin), pin, refs: caps.Select(x => x.Reference));
        }
    }
}

/// <summary>Every IC power pin has a decoupling capacitor close by.</summary>
public sealed class DecouplingRule : IRule
{
    public double MaxDistance { get; init; } = 6;
    public string Id => "decoupling";
    public string Description => "Each IC power pin has a capacitor to ground within a few millimetres.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.Where(p => p.Kind is PartKind.Ic or PartKind.Regulator).SelectMany(p => p.Pins)
                     .Where(p => p.Model is { Type: PinType.Power } m && m.Decouple != false && p.Net.Volts is not 0))
        {
            c.CapacitanceToGround(pin.Net, out var caps);
            if (caps.Count == 0)
            {
                yield return RuleHelpers.Make(Severity.Warning, Id, $"{pin.Label} has no decoupling capacitor",
                    $"No capacitor goes from {pin.Net.DisplayName} to ground.", pin);
                continue;
            }
            var nearest = caps.Select(cap => (cap, d: cap.Pins.Where(x => x.Net == pin.Net).Min(x => x.Pad.Position.DistanceTo(pin.Pad.Position))))
                              .OrderBy(x => x.d).First();
            if (nearest.d > MaxDistance)
                yield return RuleHelpers.Make(Severity.Info, Id,
                    $"{pin.Label}: nearest decoupling capacitor is {nearest.d:0.0} mm away",
                    $"{nearest.cap.Reference} ({nearest.cap.Footprint.Value}) is the closest. Keep decoupling within about {MaxDistance:0} mm of the pin.",
                    pin, refs: new[] { nearest.cap.Reference });
        }
    }
}

/// <summary>Voltage above a pin's absolute maximum.</summary>
public sealed class OvervoltageRule : IRule
{
    public string Id => "overvoltage";
    public string Description => "No pin sees more than its absolute maximum: rails, pull-ups or drivers.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.MaxVolts is not null))
        {
            var max = pin.Model!.MaxVolts!.Value;
            if (pin.Net.Volts is { } v && v > max)
            {
                yield return RuleHelpers.Make(Severity.Error, Id, $"{pin.Label} is on {pin.Net.DisplayName} ({RuleHelpers.V(v)}), max {RuleHelpers.V(max)}",
                    "The pin sits straight on a rail above its absolute maximum." + RuleHelpers.Why(pin), pin);
                continue;
            }
            foreach (var (r, rail) in c.Pulls(pin.Net).Where(x => x.Rail.Volts > max))
                yield return RuleHelpers.Make(Severity.Error, Id, $"{pin.Label} is pulled up to {rail.DisplayName} by {r.Reference}, max {RuleHelpers.V(max)}",
                    $"{r.Reference} pulls the pin to {RuleHelpers.V(rail.Volts!.Value)}." + RuleHelpers.Why(pin), pin, refs: new[] { r.Reference });
            foreach (var (driver, viaR, ratio) in RuleHelpers.Drivers(c, pin))
                if (driver.Part.SupplyVolts() is { } supply && supply * ratio is var dv && dv > max)
                    yield return RuleHelpers.Make(viaR ? Severity.Warning : Severity.Error, Id,
                        $"{driver.Label} drives {pin.Label} with {RuleHelpers.V(dv)}, max {RuleHelpers.V(max)}",
                        $"{driver.Part.Reference} runs from {RuleHelpers.V(dv)}" + (viaR ? ", through a series resistor, which limits the current but still lifts the pin above its maximum." : ".") + RuleHelpers.Why(pin),
                        pin, refs: new[] { driver.Id });
        }
    }
}

/// <summary>Logic level mismatch: a low-voltage output into an input that needs more.</summary>
public sealed class LogicLevelRule : IRule
{
    public string Id => "logic-level";
    public string Description => "Outputs reach the input-high threshold of the inputs they drive.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.Type is PinType.In && p.Model.Vih is not null))
        {
            if (RuleHelpers.Threshold(pin.Model!.Vih, pin.Part) is not { } vih) continue;
            foreach (var (driver, _, ratio) in RuleHelpers.Drivers(c, pin))
            {
                if (driver.Part.SupplyVolts() is not { } supply || supply * ratio is var voh && voh >= vih) continue;
                yield return RuleHelpers.Make(Severity.Error, Id,
                    $"{driver.Label} ({RuleHelpers.V(voh)}) cannot drive {pin.Label} high (needs {RuleHelpers.V(vih)})",
                    $"{pin.Part.Reference} runs from {RuleHelpers.V(pin.Part.SupplyVolts() ?? 0)} and needs {pin.Model.Vih} = {RuleHelpers.V(vih)} for a high. " +
                    "Use a level shifter with TTL inputs (e.g. 74AHCT) or run the input part from the lower supply.",
                    pin, refs: new[] { driver.Id });
            }
        }
    }
}

/// <summary>Tri-state outputs that leave the board on a connector with nothing to hold them.</summary>
public sealed class FloatingOutputRule : IRule
{
    public string Id => "floating-output";
    public string Description => "Lines that go high-impedance and leave the board have a pull resistor.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.SelectMany(p => p.Pins).Where(p => p.Model?.Tristate == true && !p.Net.IsNoConnect))
        {
            var nets = c.SeriesNets(pin.Net);
            var connectors = nets.SelectMany(n => n.Pins).Where(p => p.Part.Kind == PartKind.Connector).ToList();
            if (connectors.Count == 0) continue;
            if (nets.Any(n => c.Pulls(n).Any())) continue;
            var outs = string.Join(", ", connectors.Select(p => p.Id));
            yield return RuleHelpers.Make(Severity.Warning, Id,
                $"{pin.Label} floats on {outs} when it is not driven",
                $"The line leaves the board on {outs} and has no pull resistor. Add 10-47 kΩ to ground." + RuleHelpers.Why(pin),
                pin, refs: connectors.Select(p => p.Id), nets: nets.Select(n => n.Name));
        }
    }
}

/// <summary>Signals that leave the board without any surge or ESD protection.</summary>
public sealed class ConnectorProtectionRule : IRule
{
    public string Id => "esd";
    public string Description => "Signal lines on connectors have a TVS or ESD diode.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        var c = ctx.Circuit;
        foreach (var pin in c.Parts.Where(p => p.Kind == PartKind.Connector && p.Model?.Id is not ("raspberry-pi-40"))
                     .SelectMany(p => p.Pins).Where(p => !p.Net.IsRail && !p.Net.IsNoConnect && p.Model?.Type is null or PinType.Passive or PinType.Io))
        {
            var nets = c.SeriesNets(pin.Net);
            var protectedBy = nets.SelectMany(n => n.Pins).Where(p => p.Part.Kind == PartKind.Tvs).ToList();
            if (protectedBy.Count > 0) continue;
            if (!nets.SelectMany(n => n.Pins).Any(p => p.Part.Kind is PartKind.Ic)) continue; // only lines that reach a chip
            yield return RuleHelpers.Make(Severity.Info, Id, $"{pin.Id} ({pin.Net.DisplayName}) has no ESD protection",
                "The line leaves the board and reaches a chip without a TVS or ESD diode. Long cables pick up static and surges.", pin);
        }
    }
}

/// <summary>Named nets that only reach one pin.</summary>
public sealed class SinglePinNetRule : IRule
{
    public string Id => "single-pin-net";
    public string Description => "A labelled net connects at least two pins.";

    public IEnumerable<Finding> Check(CheckContext ctx)
    {
        foreach (var net in ctx.Circuit.Nets.Values.Where(n => !n.IsNoConnect && !n.Name.StartsWith("Net-(")))
        {
            var pins = net.Pins.Where(p => p.Part.Kind is not (PartKind.TestPoint or PartKind.MountingHole)).ToList();
            if (pins.Count == 1 && net.Pins.Count == 1)
                yield return RuleHelpers.Make(Severity.Warning, Id, $"{net.DisplayName} only reaches {pins[0].Label}",
                    "The net has a name, which usually means it should connect somewhere else too. Check for a misspelt label.", pins[0]);
        }
    }
}
