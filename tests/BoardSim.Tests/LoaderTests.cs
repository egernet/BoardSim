using BoardSim.Core.Model;
using Xunit.Abstractions;

namespace BoardSim.Tests;

public class LoaderTests(ITestOutputHelper output)
{
    [Fact]
    public void LoadsV4()
    {
        var b = Fixtures.V4;
        Assert.Equal(142, b.Footprints.Count);
        Assert.Equal(1394, b.Tracks.Count(t => !t.IsArc));
        Assert.Equal(413, b.Vias.Count);
        Assert.Equal(new[] { "F.Cu", "In1.Cu", "In2.Cu", "B.Cu" }, b.CopperLayers);
        var u1 = b.Find("U1")!;
        Assert.Equal("ESP32-C3FH4", u1.Value);
        Assert.All(Enumerable.Range(1, 32), n => Assert.Single(u1.PadsNumbered(n.ToString())));
        Assert.Equal("RXD", u1.PadsNumbered("27").Single().Net);
        Assert.Contains(b.Footprints, f => f.Side == Side.Back);
        var bounds = b.Bounds();
        Assert.Equal(65, bounds.Width, 1);
        Assert.Equal(56, bounds.Height, 1);
    }

    [Fact]
    public void LoadsV31WithNumberedNets()
    {
        var b = Fixtures.V31;
        Assert.NotEmpty(b.Footprints);
        // KiCad 7 refers to nets by number on tracks; they must resolve to names.
        Assert.All(b.Tracks, t => Assert.False(int.TryParse(t.Net, out _), $"track net {t.Net} not resolved"));
        Assert.Equal("/CH1", b.Find("J4")!.PadsNumbered("2").Single().Net);
    }

    [Theory]
    [InlineData("julelys_v4.ipc", true)]
    [InlineData("julelys_v31.ipc", false)]
    public void PadPositionsAndNetsMatchKiCadsIpcExport(string ipcFile, bool v4)
    {
        var board = v4 ? Fixtures.V4 : Fixtures.V31;
        var ipc = Fixtures.Ipc(ipcFile);
        Assert.NotEmpty(ipc);
        var checkedPads = 0;
        var misses = new List<string>();
        foreach (var rec in ipc)
        {
            var fp = board.Find(rec.Reference);
            if (fp is null) { misses.Add($"{rec.Reference} missing"); continue; }
            var pads = fp.PadsNumbered(rec.Pin).ToList();
            if (pads.Count == 0) { misses.Add($"{rec.Reference}.{rec.Pin} missing"); continue; }
            // KiCad writes the hole or the pad centre; either is within the pad.
            var pad = pads.OrderBy(p => p.Position.DistanceTo(rec.Position)).First();
            if (!pad.Contains(rec.Position, 0.02))
                misses.Add($"{pad.Id} at {pad.Position}, IPC says {rec.Position}");
            // IPC-D-356 upper-cases names, drops the leading '/' and keeps only the
            // last 14 characters of long ones.
            if (!rec.Net.StartsWith("N/C") &&
                !pad.Net.TrimStart('/').ToUpperInvariant().EndsWith(rec.Net.ToUpperInvariant(), StringComparison.Ordinal))
                misses.Add($"{pad.Id} net {pad.Net}, IPC says {rec.Net}");
            checkedPads++;
        }
        foreach (var m in misses.Take(20)) output.WriteLine(m);
        Assert.True(checkedPads > 100);
        Assert.Empty(misses);
    }
}
