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

    public Simulator Simulator { get; }
    public Part Mcu { get; }
    public string LedOrder { get; set; }
    public Dictionary<PinRef, LedString> Strings { get; } = new();
    public int Version { get; private set; }

    /// <summary>The latest chip time seen on the bridge, in ns (0 for bridges without time stamps).</summary>
    public long ChipTimeNs { get; private set; }

    private const int MaxEdges = 20000;
    private readonly Dictionary<int, List<(long T, bool High)>> _edges = new();
    public int Frames { get; private set; }
    public int Resets { get; private set; }

    /// <summary>Called with the input mask whenever simulated input levels change.</summary>
    public Action<uint>? SendInputs { get; set; }

    /// <summary>
    /// CHIP_EN on the board changed: false = held in reset; true = released,
    /// with the boot mode GPIO9 selects (true = download). Raised by whatever
    /// drives EN in the simulation: the RESET button, a Raspberry Pi, a pull-up.
    /// </summary>
    public Action<bool, bool>? EnableChanged { get; set; }
    private bool? _enHigh;

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
                    if (p.TimeNs is { } t) RecordEdges(t);
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

    private void RecordEdges(long t)
    {
        ChipTimeNs = Math.Max(ChipTimeNs, t);
        for (var n = 0; n < 22; n++)
        {
            var bit = 1u << n;
            if ((_enable & bit) == 0 || _route[n] != GpioSignal) continue;
            var high = (_out & bit) != 0;
            if (!_edges.TryGetValue(n, out var log)) _edges[n] = log = new List<(long, bool)>();
            if (log.Count > 0 && log[^1].High == high) continue;
            log.Add((t, high));
            if (log.Count > MaxEdges) log.RemoveRange(0, MaxEdges / 4);
        }
    }

    /// <summary>
    /// What a probe on <paramref name="net"/> sees from the firmware: the last
    /// LED frame if the net carries RMT data, or the edges of the GPIO output
    /// that drives it (also through buffers, a mux or resistors). Null when no
    /// output of the chip reaches it.
    /// </summary>
    public ScopeTrace? TraceFor(Net net)
    {
        lock (_lock)
        {
            var frames = _rmtHistory.Where(f => f.Nets.ContainsKey(net)).ToList();
            if (frames.Count > 0 && frames[^1] is var rmt && (DateTime.Now - rmt.Time).TotalSeconds < 3)
            {
                var levels = rmt.Nets[net];
                return new RmtTrace(rmt, levels.High, levels.Low)
                {
                    Source = $"{rmt.Source.Label} (RMT)", Frames = frames, NowNs = ChipTimeNs,
                };
            }

            foreach (var (n, pin) in _gpio)
            {
                if ((_enable & (1u << n)) == 0 || _route[n] != GpioSignal) continue;
                if (!_edges.TryGetValue(n, out var log) || log.Count == 0) continue;
                (double High, double Low) lv;
                if (pin.Net == net) lv = (Mcu.SupplyVolts() ?? 3.3, 0);
                else if (Follow(pin).Nets.TryGetValue(net, out var f)) lv = f;
                else continue;
                var from = Math.Max(0, log.Count - 4000);
                return new EdgeTrace(log.GetRange(from, log.Count - from), lv.High, lv.Low, ChipTimeNs) { Source = pin.Label };
            }
            return null;
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

    private void CheckEnable()
    {
        var en = Mcu.PinsNamed("CHIP_EN").Concat(Mcu.PinsNamed("EN")).FirstOrDefault();
        if (en is null || !Simulator.Nets.TryGetValue(en.Net, out var r)) return;
        var high = r.State is NetState.High;
        if (r.State is NetState.Mid or NetState.Floating) high = _enHigh ?? true;   // undecided: keep
        if (_enHigh == high) return;
        var first = _enHigh is null;
        _enHigh = high;
        if (first && high) return;   // powered up normally: nothing to do
        var io9Low = _gpio.TryGetValue(9, out var io9) && Simulator.Nets.TryGetValue(io9.Net, out var r9) && r9.State == NetState.Low;
        EnableChanged?.Invoke(high, high && io9Low);
    }

    private void PushInputs()
    {
        CheckEnable();
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
        // The firmware counts RMT ticks; how long a tick really is depends on the crystal.
        var tickNs = rmt.TickPs / 1000.0 / Simulator.ClockRatio(Mcu);
        var colors = Ws2812.Decode(rmt.Items, tickNs, LedOrder);
        var follow = Follow(source);
        if (rmt.TimeNs is { } end) ChipTimeNs = Math.Max(ChipTimeNs, end);
        LastRmt = new RmtCapture(source, rmt.Items, tickNs, DateTime.Now, follow.Nets, rmt.TimeNs);
        _rmtHistory.Add(LastRmt);
        // Keep about two seconds of chip time (enough for the slowest timebase), and never more than 1000 frames.
        while (_rmtHistory.Count > 1000 ||
               (_rmtHistory.Count > 1 && LastRmt.EndNs is { } newest && _rmtHistory[0].EndNs is { } oldest && newest - oldest > 2_200_000_000))
            _rmtHistory.RemoveAt(0);
        foreach (var target in follow.Connectors)
        {
            if (!Strings.TryGetValue(target, out var s)) Strings[target] = s = new LedString { Pin = target };
            s.Leds = colors;
            s.Frames++;
            s.LastFrame = DateTime.Now;
        }
    }

    /// <summary>Connector pins that follow <paramref name="source"/>.</summary>
    public List<PinRef> Targets(PinRef source) => Follow(source).Connectors;

    private readonly Dictionary<string, Following> _followCache = new();

    /// <summary>What a pin's signal reaches: every net that is high when it is high and low when it is low, with both voltages.</summary>
    public sealed record Following(List<PinRef> Connectors, IReadOnlyDictionary<Net, (double High, double Low)> Nets);

    /// <summary>
    /// Drives <paramref name="source"/> high, then low, with everything else as
    /// it is now, and keeps the nets that follow it.
    /// </summary>
    public Following Follow(PinRef source)
    {
        var key = $"{source.Id}:{_out:x}:{_enable:x}:{string.Join(',', _route)}:{Simulator.ClockRatio(Mcu)}";
        if (_followCache.TryGetValue(key, out var cached)) return cached;

        // What-if solves: the published state (what the user sees) is not touched.
        var before = Simulator.DriveOf(source);
        Simulator.SetPin(source, PinDrive.High);
        var high = Simulator.Solve(publish: false).Where(n => n.Value.State == NetState.High).ToDictionary(n => n.Key, n => n.Value.Volts ?? 0);
        Simulator.SetPin(source, PinDrive.Low);
        var low = Simulator.Solve(publish: false);
        var nets = new Dictionary<Net, (double, double)>();
        foreach (var (net, vh) in high)
            if (low.TryGetValue(net, out var r) && r.State == NetState.Low && !net.IsRail)
                nets[net] = (vh, r.Volts ?? 0);

        var connectors = Simulator.Circuit.Parts.Where(p => p.Kind == PartKind.Connector)
            .SelectMany(p => p.Pins)
            .Where(p => nets.ContainsKey(p.Net) && p.Part != Mcu && p.Part.Model?.Id != "raspberry-pi-40")
            .ToList();

        Simulator.SetPin(source, before);
        if (_followCache.Count > 256) _followCache.Clear();
        var result = new Following(connectors, nets);
        _followCache[key] = result;
        return result;
    }

    /// <summary>The last RMT transmission, as the oscilloscope shows it.</summary>
    public sealed record RmtCapture(PinRef Source, uint[] Items, double TickNs, DateTime Time,
        IReadOnlyDictionary<Net, (double High, double Low)> Nets, long? EndNs = null)
    {
        /// <summary>How long the transmission lasts, in ns.</summary>
        public double DurationNs { get; } = Items.Sum(i => (double)((i & 0x7fff) + ((i >> 16) & 0x7fff))) * TickNs;
        /// <summary>Chip time the transmission started, in ns.</summary>
        public long StartNs => (EndNs ?? (long)DurationNs) - (long)DurationNs;
    }

    public RmtCapture? LastRmt { get; private set; }
    private readonly List<RmtCapture> _rmtHistory = new();
}
