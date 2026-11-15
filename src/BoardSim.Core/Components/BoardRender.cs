using System.IO.Compression;
using System.Text.RegularExpressions;
using BoardSim.Core.Model;

namespace BoardSim.Core.Components;

/// <summary>
/// A picture of one side of the board as KiCad's 3D viewer draws it
/// (kicad-cli pcb render, orthographic), placed in board coordinates: the
/// image covers <see cref="Area"/> (mm, KiCad's y-down coordinates). Bottom
/// renders are seen from below, so they are mirrored left to right.
/// </summary>
public sealed record BoardRender(string Image, Side Side, bool WithParts, Rect2 Area, bool Mirrored);

public static class BoardRenders
{
    /// <summary>Pixels per millimetre to render at (capped so big boards stay below 4096 px).</summary>
    public const double PixelsPerMm = 40;

    /// <summary>
    /// Renders a side with and without the parts. The bare render gives the
    /// board outline in its alpha channel, which places both images on the board
    /// (parts may stick out over the edge, so the render with parts cannot).
    /// Cached per board version and side.
    /// </summary>
    public static async Task<(BoardRender WithParts, BoardRender Bare)?> RenderAsync(Board board, IReadOnlyList<ComponentInfo> components, Side side, CancellationToken ct = default)
    {
        if (Board3D.FindKiCadCli() is not { } cli || !File.Exists(board.FilePath)) return null;
        var (patched, key) = await Board3D.PatchedBoardAsync(board.FilePath, components, ct);
        patched = GreenMask(patched);
        var bounds = board.Bounds();
        var ppm = Math.Min(PixelsPerMm, 4000 / Math.Max(bounds.Width, bounds.Height));
        var w = (int)Math.Ceiling(bounds.Width * ppm * 1.08);
        var h = (int)Math.Ceiling(bounds.Height * ppm * 1.08);
        var name = Path.GetFileNameWithoutExtension(board.FilePath);
        var sideName = side == Side.Front ? "top" : "bottom";
        string[] Args() => new[] { "pcb", "render", "--side", sideName, "--background", "transparent", "--quality", "basic",
            "--preset", "follow_pcb_editor", "--use-board-stackup-colors", "-w", w.ToString(), "-h", h.ToString() };
        // The cache key covers the patched board and the render settings, so changing either renders again.
        var settings = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(patched + string.Join(' ', Args()))))[..12];
        var withFile = Path.Combine(Board3D.CacheDir, $"{name}-{key}-{settings}-{sideName}.png");
        var bareFile = Path.Combine(Board3D.CacheDir, $"{name}-{key}-{settings}-{sideName}-bare.png");
        var jobs = new List<Task>();
        if (!File.Exists(withFile)) jobs.Add(Board3D.RunKiCadAsync(cli, patched, key, withFile, Args(), ct));
        if (!File.Exists(bareFile)) jobs.Add(Board3D.RunKiCadAsync(cli, Board3D.WithoutModels(patched), key + "b", bareFile, Args(), ct));
        await Task.WhenAll(jobs);

        var (iw, ih, box) = Png.AlphaBounds(bareFile);
        if (box is not { } b) return null;
        var area = Place(bounds, iw, ih, b, side == Side.Back);
        return (new BoardRender(withFile, side, true, area, side == Side.Back), new BoardRender(bareFile, side, false, area, side == Side.Back));
    }

    /// <summary>A solder mask without a colour in the stackup renders brown; most boards are green, so say so (stackup layers only, not graphics on the mask layer).</summary>
    public static string GreenMaskForTest(string text) => GreenMask(text);

    internal static string GreenMask(string text) =>
        Regex.Replace(text, @"\(layer ""(F|B)\.Mask""\s*\(type ""[^""]*""\)(?:[^()]|\([^()]*\))*\)",
            m => m.Value.Contains("(color") ? m.Value : m.Value[..^1] + " (color \"Green\"))");

    /// <summary>
    /// Where the whole image lies on the board, given the pixel box the board
    /// outline fills (left, top, right, bottom, exclusive). Bottom views are
    /// mirrored: the image's left edge is the board's right edge.
    /// </summary>
    public static Rect2 Place(Rect2 board, int imageWidth, int imageHeight, (int Left, int Top, int Right, int Bottom) outline, bool mirrored)
    {
        var sx = (outline.Right - outline.Left) / board.Width;
        var sy = (outline.Bottom - outline.Top) / board.Height;
        var top = board.MinY - outline.Top / sy;
        var left = mirrored
            ? board.MinX - (imageWidth - outline.Right) / sx
            : board.MinX - outline.Left / sx;
        return new Rect2(left, top, left + imageWidth / sx, top + imageHeight / sy);
    }
}

/// <summary>Just enough PNG decoding to find the opaque part of an RGBA image.</summary>
public static class Png
{
    /// <summary>Image size and the box of pixels with alpha above 128 (exclusive right/bottom), or null when all are transparent.</summary>
    public static (int Width, int Height, (int Left, int Top, int Right, int Bottom)? Box) AlphaBounds(string path)
    {
        using var f = File.OpenRead(path);
        var sig = new byte[8];
        f.ReadExactly(sig);
        int width = 0, height = 0, colorType = 0, bitDepth = 0;
        var idat = new MemoryStream();
        var header = new byte[8];
        while (f.Read(header, 0, 8) == 8)
        {
            var len = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var data = new byte[len];
            f.ReadExactly(data);
            f.Seek(4, SeekOrigin.Current); // CRC
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                bitDepth = data[8]; colorType = data[9];
                if (data[12] != 0) throw new NotSupportedException("Interlaced PNG");
            }
            else if (type == "IDAT") idat.Write(data);
            else if (type == "IEND") break;
        }
        var channels = colorType switch { 6 => 4, 4 => 2, 2 => 3, 0 => 1, _ => throw new NotSupportedException($"PNG colour type {colorType}") };
        if (bitDepth != 8) throw new NotSupportedException($"PNG bit depth {bitDepth}");
        if (channels is 1 or 3) return (width, height, (0, 0, width, height));

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        var stride = width * channels;
        var prev = new byte[stride];
        var row = new byte[stride];
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var filter = z.ReadByte();
            z.ReadExactly(row);
            Unfilter(filter, row, prev, channels);
            for (var x = 0; x < width; x++)
                if (row[x * channels + channels - 1] > 128)
                {
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    bottom = y;
                }
            (prev, row) = (row, prev);
        }
        return right < 0 ? (width, height, null) : (width, height, (left, top, right + 1, bottom + 1));
    }

    private static void Unfilter(int filter, byte[] row, byte[] prev, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
            row[i] = filter switch
            {
                1 => (byte)(row[i] + a),
                2 => (byte)(row[i] + b),
                3 => (byte)(row[i] + (a + b) / 2),
                4 => (byte)(row[i] + Paeth(a, b, c)),
                _ => row[i],
            };
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
