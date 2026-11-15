using System.IO.Compression;
using BoardSim.Core.Components;
using BoardSim.Core.Model;

namespace BoardSim.Tests;

public class BoardRenderTests
{
    /// <summary>An RGBA PNG with an opaque rectangle, written by hand (filter 0 rows, zlib).</summary>
    private static string WritePng(int w, int h, (int L, int T, int R, int B) opaque)
    {
        var raw = new MemoryStream();
        for (var y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (var x = 0; x < w; x++)
            {
                var on = x >= opaque.L && x < opaque.R && y >= opaque.T && y < opaque.B;
                raw.Write(new byte[] { 10, 80, 30, (byte)(on ? 255 : 0) });
            }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zs);
        var path = Path.Combine(Path.GetTempPath(), $"boardsim-{Guid.NewGuid():N}.png");
        using var f = File.Create(path);
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            f.Write(BitConverter.GetBytes(data.Length).Reverse().ToArray());
            f.Write(System.Text.Encoding.ASCII.GetBytes(type));
            f.Write(data);
            f.Write(new byte[4]); // CRC is not checked
        }
        var ihdr = BitConverter.GetBytes(w).Reverse().Concat(BitConverter.GetBytes(h).Reverse()).Concat(new byte[] { 8, 6, 0, 0, 0 }).ToArray();
        Chunk("IHDR", ihdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void FindsTheOpaqueBoardInARender()
    {
        var png = WritePng(200, 120, (30, 20, 170, 100));
        try
        {
            var (w, h, box) = Png.AlphaBounds(png);
            Assert.Equal((200, 120), (w, h));
            Assert.Equal((30, 20, 170, 100), box);
        }
        finally { File.Delete(png); }
    }

    [Fact]
    public void PlacesTheImageOnTheBoard()
    {
        // A 70 x 40 mm board drawn 140 x 80 px inside a 200 x 120 image: 2 px/mm, 30 px margin left, 20 px top.
        var board = new Rect2(10, 5, 80, 45);
        var area = BoardRenders.Place(board, 200, 120, (30, 20, 170, 100), mirrored: false);
        Assert.Equal(new Rect2(-5, -5, 95, 55), area);
        // Seen from below the image is mirrored: its right margin (30 px) is on the board's left.
        var bottom = BoardRenders.Place(board, 200, 120, (30, 20, 170, 100), mirrored: true);
        Assert.Equal(-5, bottom.MinX, 6);
        // Asymmetric margins show the difference: 10 px left, 50 px right.
        var skewed = BoardRenders.Place(board, 200, 120, (10, 20, 150, 100), mirrored: true);
        Assert.Equal(10 - 25, skewed.MinX, 6);
        Assert.Equal(-15 + 100, skewed.MaxX, 6);   // 200 px at 2 px/mm
    }

    [Fact]
    public void GivesAColourlessMaskGreenButLeavesOthers()
    {
        const string stack = "(stackup (layer \"F.Mask\" (type \"Top Solder Mask\") (thickness 0.01)) " +
                             "(layer \"B.Mask\" (type \"Bottom Solder Mask\") (color \"Black\") (thickness 0.01)))";
        var text = stack + " (gr_line (start 0 0) (end 1 1) (layer \"F.Mask\"))";
        var green = BoardRenders.GreenMaskForTest(text);
        Assert.Contains("(type \"Top Solder Mask\") (thickness 0.01) (color \"Green\"))", green);
        Assert.Contains("(color \"Black\")", green);
        Assert.DoesNotContain("(color \"Green\") (color", green);
        Assert.EndsWith("(layer \"F.Mask\"))", green);   // graphics on the mask layer are not touched
    }
}
