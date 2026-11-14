using BoardSim.Core.Analysis;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

namespace BoardSim.Core.Emulation;

/// <summary>The LEDs on one connector pin, as last sent by the firmware.</summary>
public sealed class LedString
{
    public required PinRef Pin { get; init; }
    public IReadOnlyList<LedColor> Leds { get; set; } = Array.Empty<LedColor>();
    public int Frames { get; set; }
    public DateTime LastFrame { get; set; }
}

/// <summary>
/// Joins a running firmware to the simulated board: the chip's outputs drive
/// the simulation, the simulated levels go back to its inputs, and LED data
/// from the RMT is followed through the circuit to the connector it reaches.
/// </summary>
public sealed class EmulatedBoard
{
    public const int RmtSignal0 = 51;
    public const int GpioSignal = 128;

    private readonly object _lock = new();
    private readonly Dictionary<int, PinRef> _gpio = new();
    private readonly int[] _route = Enumerable.Repeat(GpioSignal, 22).ToArray();
    private uint _out, _enable, _sentInputs = uint.MaxValue;
    private readonly Dictionary<string, List<PinRef>> _targetCache = new();

    public Simulator Simulator { get; }
    public Part Mcu { get; }
    public string LedOrder { get; set; }
    public Dictionary<PinRef, LedString> Strings { get; } = new();
    public int Version { get; private set; }
    public int Frames { get; private set; }
    public int Resets { get; private set; }

    /// <summary>Called with the input mask whenever simulated input levels change.</summary>
    public Action<uint>? SendInputs { get; set; }

    public EmulatedBoard(Simulator simulator, Part mcu, string ledOrder = "GRB")
    {
        Simulator = simulator;
        Mcu = mcu;
        LedOrder = ledOrder;
        for (var n = 0; n < 22; n++)
            if (mcu.PinsNamed($"GPIO{n}").FirstOrDefault() is { } pin) _gpio[n] = pin;
    }

    /// <summary>The MCU the firmware runs on: the first part with an "mcu" behaviour.</summary>
    public static Part? FindMcu(Circuit circuit) =>
        circuit.Parts.FirstOrDefault(p => p.Model?.Behavior?.Type == "mcu" && p.Model.Id.StartsWith("esp32-c3"));

    public void Handle(BridgeMessage message)
    {
        lock (_lock)
        {
            switch (message)
            {
                case BridgeMessage.Reset:
                    Resets++;
                    _out = _enable = 0;
                    Array.Fill(_route, GpioSignal);
                    ApplyPins();
                    break;
                case BridgeMessage.Pins p:
                    _out = p.Out;
                    _enable = p.Enable;
                    ApplyPins();
                    break;
                case BridgeMessage.Route r when r.Gpio is >= 0 and < 22:
                    _route[r.Gpio] = r.Signal;
                    ApplyPins();
                    break;
                case BridgeMessage.Rmt rmt:
                    HandleRmt(rmt);
                    break;
            }
            Version++;
        }
    }

    public T Read<T>(Func<EmulatedBoard, T> read)
    {
        lock (_lock) return read(this);
    }

    /// <summary>Re-solves after the user changed something (a button) and updates the chip's inputs.</summary>
    public void Rerun()
    {
        lock (_lock)
        {
            Simulator.Run();
            PushInputs();
            Version++;
        }
    }

    private void ApplyPins()
    {
        foreach (var (n, pin) in _gpio)
        {
            var bit = 1u << n;
            if (_route[n] is >= RmtSignal0 and < RmtSignal0 + 2)
                Simulator.SetPin(pin, PinDrive.Low);          // RMT idles low between frames
            else if ((_enable & bit) != 0)
                Simulator.SetPin(pin, (_out & bit) != 0 ? PinDrive.High : PinDrive.Low);
            else
                Simulator.SetPin(pin, PinDrive.Input);
        }
        Simulator.Run();
        PushInputs();
    }

    private void PushInputs()
    {
        uint mask = 0;
        foreach (var (n, pin) in _gpio)
            if (Simulator.Nets.TryGetValue(pin.Net, out var r) && r.State == NetState.High) mask |= 1u << n;
        if (mask != _sentInputs)
        {
            _sentInputs = mask;
            SendInputs?.Invoke(mask);
        }
    }

    private void HandleRmt(BridgeMessage.Rmt rmt)
    {
        Frames++;
        if (!_gpio.TryGetValue(rmt.Gpio, out var source)) return;
        var colors = Ws2812.Decode(rmt.Items, rmt.TickPs / 1000.0, LedOrder);
        foreach (var target in Targets(source))
        {
            if (!Strings.TryGetValue(target, out var s)) Strings[target] = s = new LedString { Pin = target };
            s.Leds = colors;
            s.Frames++;
            s.LastFrame = DateTime.Now;
        }
    }

    /// <summary>
    /// Connector pins that follow <paramref name="source"/>: high when it is
    /// driven high, low when it is driven low, with everything else as it is now.
    /// </summary>
    public List<PinRef> Targets(PinRef source)
    {
        var key = $"{source.Id}:{_out:x}:{_enable:x}:{string.Join(',', _route)}";
        if (_targetCache.TryGetValue(key, out var cached)) return cached;

        var connectorPins = Simulator.Circuit.Parts.Where(p => p.Kind == PartKind.Connector)
            .SelectMany(p => p.Pins).Where(p => !p.Net.IsRail && !p.Net.IsNoConnect).ToList();
        var before = Simulator.DriveOf(source);

        Simulator.SetPin(source, PinDrive.High);
        Simulator.Run();
        var high = connectorPins.Where(p => Simulator.Nets.TryGetValue(p.Net, out var r) && r.State == NetState.High).ToHashSet();
        Simulator.SetPin(source, PinDrive.Low);
        Simulator.Run();
        var result = connectorPins.Where(p => high.Contains(p) && Simulator.Nets.TryGetValue(p.Net, out var r) && r.State == NetState.Low)
            .Where(p => p.Part != Mcu && p.Part.Model?.Id != "raspberry-pi-40")
            .ToList();

        Simulator.SetPin(source, before);
        Simulator.Run();
        if (_targetCache.Count > 256) _targetCache.Clear();
        _targetCache[key] = result;
        return result;
    }
}
