using System.Globalization;
using System.Text.RegularExpressions;
using BoardSim.Core.KiCad;
using BoardSim.Core.Model;

namespace BoardSim.Tests;

/// <summary>
/// Julelys v4 (KiCad 10) and v3.1 (KiCad 7) as test boards, plus the IPC-D-356
/// netlists KiCad exported for them. The IPC files are an independent record
/// of where every pad sits and which net it is on.
/// </summary>
public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "fixtures")))
                dir = Path.GetDirectoryName(dir);
            return Path.Combine(dir ?? throw new DirectoryNotFoundException("tests/fixtures"), "tests", "fixtures");
        }
    }

    private static readonly Lazy<Board> V4Lazy = new(() => KiCadPcbLoader.Load(Path.Combine(Dir, "julelys_v4.kicad_pcb")));
    private static readonly Lazy<Board> V31Lazy = new(() => KiCadPcbLoader.Load(Path.Combine(Dir, "julelys_v31.kicad_pcb")));

    public static Board V4 => V4Lazy.Value;
    public static Board V31 => V31Lazy.Value;

    public sealed record IpcPad(string Net, string Reference, string Pin, Vec2 Position);

    public static List<IpcPad> Ipc(string file)
    {
        var lines = File.ReadAllLines(Path.Combine(Dir, file));
        var aliases = new Dictionary<string, string>();
        var inch = lines.Any(l => l.StartsWith("P  UNITS CUST 0"));
        var scale = inch ? 0.00254 : 0.001;
        var result = new List<IpcPad>();
        var alias = new Regex(@"^P  NNAME(\S+)\s+(.+?)\s*$");
        var rec = new Regex(@"^3[1-6]7(\S*)\s+(\S+)\s+-(\S+)\s.*?X([+-]\d+)Y([+-]\d+)");
        foreach (var line in lines)
        {
            if (alias.Match(line) is { Success: true } a) { aliases[a.Groups[1].Value] = a.Groups[2].Value; continue; }
            var m = rec.Match(line);
            if (!m.Success) continue;
            var net = m.Groups[1].Value.TrimStart('/');
            if (aliases.TryGetValue(net, out var full)) net = full;
            net = Regex.Replace(net, @"#\d+$", ""); // KiCad tells truncated duplicates apart with #1, #2
            var x = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) * scale;
            var y = -int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) * scale;
            result.Add(new IpcPad(net, m.Groups[2].Value, m.Groups[3].Value, new Vec2(x, y)));
        }
        return result;
    }
}
