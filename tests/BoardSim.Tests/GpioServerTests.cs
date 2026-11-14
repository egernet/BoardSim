using System.Net.Sockets;
using System.Text;
using BoardSim.Core.Analysis;
using BoardSim.Core.Emulation;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

namespace BoardSim.Tests;

public class GpioServerTests
{
    private sealed class Line(TcpClient tcp)
    {
        private readonly StreamReader _r = new(tcp.GetStream(), Encoding.ASCII);
        public void Send(string s) => tcp.GetStream().Write(Encoding.ASCII.GetBytes(s + "\n"));
        public async Task<string> Next()
        {
            var t = _r.ReadLineAsync();
            if (await Task.WhenAny(t, Task.Delay(3000)) != t) throw new TimeoutException("no line from the server");
            return t.Result!;
        }
        public async Task<string> Until(Func<string, bool> match)
        {
            for (var i = 0; i < 200; i++) { var l = await Next(); if (match(l)) return l; }
            throw new InvalidOperationException("never matched");
        }
    }

    private static (Simulator, GpioServer, Line) Start()
    {
        var sim = new Simulator(Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn()));
        sim.Run();
        var lockObj = new object();
        var server = new GpioServer(sim, a => { lock (lockObj) { a(); sim.Run(); } }, port: 0);
        var line = new Line(new TcpClient("127.0.0.1", server.Port));
        return (sim, server, line);
    }

    [Fact]
    public async Task APiPinDrivesTheBoard()
    {
        var (sim, server, c) = Start();
        using var _ = server;
        Assert.StartsWith("boardsim gpio", await c.Next());

        // GPIO5 on the Pi (J1 pin 29) holds the ESP32 in reset through R28.
        c.Send("set GPIO5 low");
        Assert.StartsWith("ok J1.29 low", await c.Next());
        Assert.Equal(NetState.Low, sim.Nets[sim.Circuit.Nets["CHIP_UP"]].State);

        c.Send("set J1.29 input");
        await c.Next();
        Assert.Equal(NetState.High, sim.Nets[sim.Circuit.Nets["CHIP_UP"]].State);   // R4 pulls EN up again

        c.Send("get J1.GPIO5");
        Assert.StartsWith("J1.29 high", await c.Next());
    }

    [Fact]
    public async Task WatchReportsWhatTheChipDoes()
    {
        var (sim, server, c) = Start();
        using var _ = server;
        await c.Next();
        c.Send("watch J1.24");                       // SPI CS from the Pi side
        await c.Until(l => l.StartsWith("ok watching"));

        // The ESP32 drives GPIO10 (CS) high; the Pi side sees it.
        lock (sim) { sim.SetPin(ExpectationRule.Resolve(sim.Circuit, "U1.GPIO10")!, PinDrive.High); sim.Run(); }
        Assert.StartsWith("change J1.24 high 3.30", await c.Until(l => l.StartsWith("change J1.24")));
    }

    [Fact]
    public async Task FirmwarePinsCannotBeDrivenFromOutside()
    {
        var (sim, server, c) = Start();
        using var _ = server;
        server.FirmwarePart = sim.Circuit.Part("U1");
        await c.Next();
        c.Send("set U1.GPIO7 high");
        Assert.StartsWith("err U1.13 belongs to the running firmware", await c.Next());
        c.Send("set nonsense high");
        Assert.StartsWith("err unknown pin", await c.Next());
    }
}

public class ChipResetTests
{
    [Fact]
    public void ResetAndBootButtonsReachTheChip()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var sim = new Simulator(circuit);
        var board = new EmulatedBoard(sim, EmulatedBoard.FindMcu(circuit)!);
        var events = new List<(bool High, bool Download)>();
        board.EnableChanged = (h, d) => events.Add((h, d));
        board.Handle(new BridgeMessage.Pins(0, 0));

        // Hold BOOT (SW1, GPIO9 low), press and release RESET (SW2, CHIP_EN low).
        sim.SetPressed(circuit.Part("SW1")!, true);
        sim.SetPressed(circuit.Part("SW2")!, true);
        board.Rerun();
        sim.SetPressed(circuit.Part("SW2")!, false);
        board.Rerun();
        Assert.Equal(new[] { (false, false), (true, true) }, events);

        // RESET alone: boots from flash.
        sim.SetPressed(circuit.Part("SW1")!, false);
        sim.SetPressed(circuit.Part("SW2")!, true); board.Rerun();
        sim.SetPressed(circuit.Part("SW2")!, false); board.Rerun();
        Assert.Equal((true, false), events[^1]);
    }

    [Fact]
    public void APiOnTheHeaderCanResetTheChip()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var sim = new Simulator(circuit);
        var board = new EmulatedBoard(sim, EmulatedBoard.FindMcu(circuit)!);
        var events = new List<(bool, bool)>();
        board.EnableChanged = (h, d) => events.Add((h, d));
        board.Handle(new BridgeMessage.Pins(0, 0));
        // Pi GPIO5 (J1.29) to EN through R28; GPIO6 (J1.31) to GPIO9 through R29.
        sim.SetPin(ExpectationRule.Resolve(circuit, "J1.31")!, PinDrive.Low, 0);
        sim.SetPin(ExpectationRule.Resolve(circuit, "J1.29")!, PinDrive.Low, 0);
        board.Rerun();
        sim.SetPin(ExpectationRule.Resolve(circuit, "J1.29")!, PinDrive.Input);
        board.Rerun();
        Assert.Equal(new[] { (false, false), (true, true) }, events);
    }
}
