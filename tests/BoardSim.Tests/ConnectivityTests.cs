using BoardSim.Core.Analysis;
using BoardSim.Core.KiCad;
using Xunit.Abstractions;

namespace BoardSim.Tests;

public class ConnectivityTests(ITestOutputHelper output)
{
    [Fact]
    public void V4IsFullyRouted()
    {
        // KiCad's DRC reports 0 unconnected pads on v4.
        var c = Connectivity.Analyze(Fixtures.V4);
        foreach (var (net, islands) in c.Unrouted)
            output.WriteLine($"{net}: {string.Join(" | ", islands.Select(i => string.Join(",", i.Pads.Select(p => p.Id))))}");
        Assert.Empty(c.Unrouted);
    }

    [Fact]
    public void V31IsFullyRouted()
    {
        var c = Connectivity.Analyze(Fixtures.V31);
        foreach (var (net, islands) in c.Unrouted)
            output.WriteLine($"{net}: {string.Join(" | ", islands.Select(i => string.Join(",", i.Pads.Select(p => p.Id))))}");
        Assert.Empty(c.Unrouted);
    }

    [Fact]
    public void FindsACutTrack()
    {
        // Remove the track that leaves R14 (CH1 series resistor) towards J4.
        var text = File.ReadAllText(Path.Combine(Fixtures.Dir, "julelys_v4.kicad_pcb"));
        var board = KiCadPcbLoader.Parse(text);
        var r14 = board.Find("R14")!.PadsNumbered("2").Single();
        var cut = board.Tracks.Where(t => t.Net == r14.Net && (r14.Contains(t.Start, 0.05) || r14.Contains(t.End, 0.05))).ToList();
        Assert.NotEmpty(cut);
        foreach (var t in cut) board.Tracks.Remove(t);

        var c = Connectivity.Analyze(board);
        var (net, islands) = Assert.Single(c.Unrouted);
        Assert.Equal(r14.Net, net);
        Assert.Contains(islands, i => i.Pads.Count == 1 && i.Pads[0] == r14);
    }
}
