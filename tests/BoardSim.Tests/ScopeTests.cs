using BoardSim.Core.Analysis;
using BoardSim.Core.Emulation;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

namespace BoardSim.Tests;

public class ScopeTests
{
    [Fact]
    public void ParsesTimeStamps()
    {
        Assert.Equal(new BridgeMessage.Pins(0x10, 0x1b0, 123456789), BridgeMessage.Parse("O 10 1b0 123456789"));
        var r = Assert.IsType<BridgeMessage.Rmt>(BridgeMessage.Parse("R 7 100000 1 00098003 5000"));
        Assert.Equal(5000, r.TimeNs);
        // Old bridges without time stamps still parse.
        Assert.Null(((BridgeMessage.Pins)BridgeMessage.Parse("O 10 1b0")!).TimeNs);
    }

    [Fact]
    public void MeasuresASquareWave()
    {
        // 1 kHz, 25 % duty: high 250 us, low 750 us.
        var edges = new List<(long, bool)>();
        for (var i = 0; i < 10; i++) { edges.Add((i * 1_000_000L, true)); edges.Add((i * 1_000_000L + 250_000, false)); }
        var m = Measurement.Of(new EdgeTrace(edges, 5, 0, 10_000_000));
        Assert.Equal(1000, m.Hz!.Value, 1);
        Assert.Equal(25, m.DutyPercent!.Value, 1);
        Assert.Equal(0.0005, m.AutoPerDivSeconds());      // 4 periods over 10 divisions -> 0.4 ms, rounded up to 0.5 ms
    }

    [Fact]
    public void MeasuresLedData()
    {
        var f = (BridgeMessage.Rmt)BridgeMessage.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "rmt_frame_julelys_rain.txt")).Trim())!;
        var capture = new EmulatedBoard.RmtCapture(null!, f.Items, f.TickPs / 1000.0, DateTime.Now, new Dictionary<Net, (double, double)>());
        var m = Measurement.Of(new RmtTrace(capture, 3.3, 0));
        Assert.InRange(m.Hz!.Value, 832_000, 835_000);   // 12 ticks of 100 ns per bit
    }

    [Fact]
    public void AGpioSeenAfterTheLevelShifterHasItsVoltage()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var board = new EmulatedBoard(new Simulator(circuit), EmulatedBoard.FindMcu(circuit)!);
        board.Handle(new BridgeMessage.Pins(0x000, 0x1b0, 1000));
        board.Handle(new BridgeMessage.Pins(0x010, 0x1b0, 2000));   // GPIO4 high
        board.Handle(new BridgeMessage.Pins(0x000, 0x1b0, 3000));
        board.Handle(new BridgeMessage.Pins(0x010, 0x1b0, 4000));

        // AS0 is GPIO4 after the 74AHCT1G125: the probe sees 5 V edges, not 3.3 V.
        var t = Assert.IsType<EdgeTrace>(board.TraceFor(circuit.Nets["/AS0"]));
        Assert.Equal(5.0, t.High, 1);
        Assert.Equal(new[] { 1000L, 2000, 3000, 4000 }, t.Edges.Select(e => e.T));
        Assert.Equal(500_000, Measurement.Of(t).Hz!.Value, 1);
        Assert.Equal(3.3, Assert.IsType<EdgeTrace>(board.TraceFor(circuit.Nets["A0"])).High, 1);
    }

    [Theory]
    [InlineData(0.0004, 0.0005)]
    [InlineData(0.0011, 0.002)]
    [InlineData(3e-7, 5e-7)]
    public void SnapsToOneTwoFive(double raw, double expected) => Assert.Equal(expected, Measurement.Snap(raw), 12);
}
