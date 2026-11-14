using BoardSim.Core.Analysis;
using BoardSim.Core.Parts;

namespace BoardSim.Core.Simulation;

/// <summary>How a part acts in the simulation; picked from the part model's "behavior".</summary>
public interface IPartBehavior
{
    Part Part { get; }
    void Reset();
    /// <summary>Adds the part's current state to the circuit: switches, driven outputs, pulls.</summary>
    void Stamp(Simulator.StampContext ctx);
    /// <summary>Reads the solved voltages; returns true when the outputs change.</summary>
    bool Update(Simulator.ReadContext ctx);
}

public static class BehaviorFactory
{
    public static IPartBehavior? Create(Part part) => part.Model?.Behavior?.Type switch
    {
        "analogMux" => new AnalogMux(part, part.Model.Behavior),
        "buffer" => new Buffer(part, part.Model.Behavior),
        "mcu" => new Mcu(part),
        _ => null,
    };

    /// <summary>Logic level of an input: true, false, or null when undefined or floating.</summary>
    public static bool? Read(double volts, double vih, double vil) =>
        double.IsNaN(volts) ? null : volts >= vih ? true : volts <= vil ? false : null;

    /// <summary>Input thresholds of a pin: the model's VIH, VIL defaults to the mirror of it.</summary>
    public static (double Vih, double Vil) Thresholds(PinRef pin)
    {
        var supply = pin.Part.SupplyVolts() ?? 5;
        var vih = Analysis.RuleHelpersPublic.Threshold(pin.Model?.Vih, pin.Part) ?? 0.7 * supply;
        var vil = pin.Model?.Vih is { } s && !s.Contains('*') ? Math.Min(0.8, vih * 0.4) : supply - vih;
        return (vih, Math.Min(vil, vih - 0.01));
    }
}

/// <summary>74HC4051-style analog multiplexer: the common pin connects to one channel.</summary>
internal sealed class AnalogMux(Part part, BehaviorModel m) : IPartBehavior
{
    public Part Part { get; } = part;
    private int? _channel;

    public void Reset() => _channel = null;

    private PinRef? Pin(string? name) => name is null ? null : Part.PinsNamed(name).FirstOrDefault();

    public void Stamp(Simulator.StampContext ctx)
    {
        if (_channel is not { } ch || Pin(m.Common) is not { } common) return;
        if (m.Channels is null || ch >= m.Channels.Count || Pin(m.Channels[ch]) is not { } y) return;
        ctx.Conductance(common.Net, y.Net, 1 / (m.OnResistance ?? 80));
    }

    public bool Update(Simulator.ReadContext ctx)
    {
        int? channel = 0;
        if (Pin(m.Enable) is { } en)
        {
            var (vih, vil) = BehaviorFactory.Thresholds(en);
            var level = BehaviorFactory.Read(ctx.Volts(en.Net), vih, vil);
            var active = m.EnableActive == "high";
            if (level is null || level != active) channel = null;
        }
        if (channel is not null)
        {
            for (var bit = 0; bit < (m.Select?.Count ?? 0); bit++)
            {
                if (Pin(m.Select![bit]) is not { } s) { channel = null; break; }
                var (vih, vil) = BehaviorFactory.Thresholds(s);
                var level = BehaviorFactory.Read(ctx.Volts(s.Net), vih, vil);
                if (level is null) { channel = null; break; }
                if (level == true) channel |= 1 << bit;
            }
        }
        if (channel == _channel) return false;
        _channel = channel;
        return true;
    }

    public int? Channel => _channel;
}

/// <summary>Tri-state buffer: output follows the input while enabled, else high impedance.</summary>
internal sealed class Buffer(Part part, BehaviorModel m) : IPartBehavior
{
    public Part Part { get; } = part;
    private bool? _out;      // null = not driving
    private bool _undefined; // enabled but the input is between thresholds or floating

    public void Reset() { _out = null; _undefined = false; }

    private PinRef? Pin(string? name) => name is null ? null : Part.PinsNamed(name).FirstOrDefault();

    public void Stamp(Simulator.StampContext ctx)
    {
        if (_out is not { } high || Pin(m.Output) is not { } y) return;
        var supply = Part.SupplyVolts() ?? 5;
        ctx.Drive(y.Net, high ? supply : 0, 1 / 15.0, $"{y.Label}");
    }

    public bool Update(Simulator.ReadContext ctx)
    {
        bool? output = null;
        var enabled = true;
        if (Pin(m.Enable) is { } en)
        {
            var (vih, vil) = BehaviorFactory.Thresholds(en);
            var level = BehaviorFactory.Read(ctx.Volts(en.Net), vih, vil);
            enabled = level is not null && level == (m.EnableActive == "high");
        }
        var undefined = false;
        if (enabled && Pin(m.Input) is { } a)
        {
            var (vih, vil) = BehaviorFactory.Thresholds(a);
            output = BehaviorFactory.Read(ctx.Volts(a.Net), vih, vil);
            undefined = output is null;
        }
        if (output == _out && undefined == _undefined) return false;
        _out = output;
        _undefined = undefined;
        return true;
    }
}

/// <summary>
/// A microcontroller without firmware: its I/O pins are inputs with the
/// model's internal pulls, unless the user (or an emulator) drives them.
/// </summary>
internal sealed class Mcu(Part part) : IPartBehavior
{
    public Part Part { get; } = part;
    public void Reset() { }

    public void Stamp(Simulator.StampContext ctx)
    {
        var supply = Part.SupplyVolts() ?? 3.3;
        foreach (var pin in Part.Pins.Where(p => p.Model?.InternalPull is not null))
        {
            if (ctx.Simulator.DriveOf(pin) != PinDrive.Input) continue;
            // Internal pulls are roughly 45 kΩ on the ESP32 family.
            ctx.Drive(pin.Net, pin.Model!.InternalPull == "up" ? supply : 0, 1 / 45e3, $"{pin.Label} internal pull", strong: false);
        }
    }

    public bool Update(Simulator.ReadContext ctx) => false;
}
