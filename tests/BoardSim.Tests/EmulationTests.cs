using BoardSim.Core.Analysis;
using BoardSim.Core.Emulation;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

namespace BoardSim.Tests;

public class EmulationTests
{
    private static BridgeMessage.Rmt RecordedFrame() =>
        (BridgeMessage.Rmt)BridgeMessage.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "rmt_frame_julelys_rain.txt")).Trim())!;

    [Fact]
    public void ParsesBridgeLines()
    {
        Assert.Equal(new BridgeMessage.Pins(0x10, 0x1b0), BridgeMessage.Parse("O 10 1b0"));
        Assert.Equal(new BridgeMessage.Route(7, 51), BridgeMessage.Parse("M 7 51"));
        Assert.IsType<BridgeMessage.Reset>(BridgeMessage.Parse("B"));
        var r = Assert.IsType<BridgeMessage.Rmt>(BridgeMessage.Parse("R 7 100000 2 0009800300038009"));
        Assert.Equal(new uint[] { 0x00098003, 0x00038009 }, r.Items);
        Assert.Null(BridgeMessage.Parse("X junk"));
    }

    [Fact]
    public void DecodesWs2812Bits()
    {
        // 10 MHz ticks: 0 = 3 high + 9 low, 1 = 9 high + 3 low (led_strip's timing).
        uint zero = 0x00098003, one = 0x00038009;
        var bits = new List<uint>();
        void Byte(int v) { for (var i = 7; i >= 0; i--) bits.Add(((v >> i) & 1) != 0 ? one : zero); }
        Byte(0x00); Byte(0xff); Byte(0x10);         // G R B
        bits.Add(0x01f401f4);                        // reset: 50 us low
        var leds = Ws2812.Decode(bits, 100, "GRB");
        Assert.Equal(new LedColor(0xff, 0x00, 0x10), Assert.Single(leds));
        Assert.Equal(new LedColor(0x00, 0xff, 0x10), Ws2812.Decode(bits, 100, "RGB").Single());
    }

    [Fact]
    public void DecodesARecordedJulelysFrame()
    {
        // One frame from the Julelys firmware's RainTask, captured from the QEMU fork.
        var f = RecordedFrame();
        Assert.Equal(7, f.Gpio);
        var leds = Ws2812.Decode(f.Items, f.TickPs / 1000.0, "RGB");
        Assert.Equal(125, leds.Count);
        Assert.Contains(leds, l => l.R + l.G + l.B > 0);
    }

    [Fact]
    public void FirmwareMuxSelectRoutesFramesToTheRightConnector()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var sim = new Simulator(circuit);
        var mcu = EmulatedBoard.FindMcu(circuit)!;
        var board = new EmulatedBoard(sim, mcu, "RGB");

        // What the firmware does: GPIO7 to the RMT, GPIO4/5/8 outputs; channel 1 = GPIO4 high.
        board.Handle(new BridgeMessage.Route(7, 51));
        board.Handle(new BridgeMessage.Pins(0x10, 0x1b0));
        board.Handle(RecordedFrame());

        var s = Assert.Single(board.Strings.Values);
        Assert.Equal("J5.2", s.Pin.Id);       // channel 1 = CH2 = J5
        Assert.Equal(125, s.Leds.Count);

        board.Handle(new BridgeMessage.Pins(0x100, 0x1b0));   // GPIO8: channel 4 = CH5 = J8
        board.Handle(RecordedFrame());
        Assert.Contains(board.Strings.Values, x => x.Pin.Id == "J8.2");
    }

    [Fact]
    public void ButtonsReachTheChipAsInputs()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var sim = new Simulator(circuit);
        var board = new EmulatedBoard(sim, EmulatedBoard.FindMcu(circuit)!);
        uint inputs = 0;
        board.SendInputs = m => inputs = m;
        board.Handle(new BridgeMessage.Pins(0, 0));
        Assert.NotEqual(0u, inputs & (1u << 9));           // GPIO9 pulled up
        sim.SetPressed(circuit.Part("SW1")!, true);
        board.Rerun();
        Assert.Equal(0u, inputs & (1u << 9));              // BOOT pressed: GPIO9 low
    }
}
