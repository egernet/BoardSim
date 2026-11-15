using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;

namespace BoardSim.Core.Components;

/// <summary>Triangles with a normal and a colour per vertex, in millimetres; Z points up out of the board's top side.</summary>
public sealed class Mesh3D
{
    public required float[] Positions { get; init; }
    public required float[] Normals { get; init; }
    public required float[] Colors { get; init; }
    public required uint[] Indices { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }
    /// <summary>"KiCad (STEP models)" or "boxes (KiCad not found)".</summary>
    public string Source { get; init; } = "";
    public int Triangles => Indices.Length / 3;
}

/// <summary>
/// The board in 3D with the real component models. KiCad turns the STEP
/// models into triangles (kicad-cli pcb export glb); BoardSim first points
/// every footprint at the model file it found (project folder, KiCad's
/// library, third-party libraries), so boards from older KiCad versions and
/// other machines still get their models. The result is cached per board
/// version. Without KiCad, parts are drawn as boxes at their real height.
/// </summary>
public static class Board3D
{
    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".boardsim", "3d");

    public static async Task<Mesh3D> BuildAsync(Circuit circuit, IReadOnlyList<ComponentInfo> components, Action<string>? progress = null, CancellationToken ct = default)
    {
        var board = circuit.Board;
        if (FindKiCadCli() is { } cli && File.Exists(board.FilePath))
        {
            try
            {
                var glb = await ExportGlbAsync(cli, board.FilePath, components, progress, ct);
                return Glb.Load(glb) is { } mesh ? mesh : Boxes(circuit, components, "boxes (KiCad export was empty)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Boxes(circuit, components, $"boxes (KiCad export failed: {e.Message})");
            }
        }
        return Boxes(circuit, components, "boxes (install KiCad 8 or newer for the real 3D models)");
    }

    public static string? FindKiCadCli()
    {
        var exe = OperatingSystem.IsWindows() ? "kicad-cli.exe" : "kicad-cli";
        var candidates = new List<string?> { Environment.GetEnvironmentVariable("BOARDSIM_KICAD_CLI") };
        if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/KiCad/KiCad.app/Contents/MacOS/kicad-cli");
            try { candidates.AddRange(Directory.GetDirectories("/Applications", "KiCad*").OrderDescending().Select(d => Path.Combine(d, "KiCad.app/Contents/MacOS/kicad-cli"))); } catch (Exception) { }
        }
        else if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            try { candidates.AddRange(Directory.GetDirectories(Path.Combine(pf, "KiCad")).OrderDescending().Select(d => Path.Combine(d, "bin", exe))); } catch (Exception) { }
        }
        else candidates.AddRange(new[] { "/usr/bin/kicad-cli", "/usr/local/bin/kicad-cli" });
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0) candidates.Add(Path.Combine(dir, exe));
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }

    /// <summary>The board file with every model path pointed at the file found for it, and a key for the cache.</summary>
    internal static async Task<(string Text, string Key)> PatchedBoardAsync(string boardPath, IReadOnlyList<ComponentInfo> components, CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var c in components)
            if (c.Model3D is { File: { } file } m) map[m.Model.Path] = file;
        var resolver = new ModelResolver(Path.GetDirectoryName(Path.GetFullPath(boardPath))!);
        foreach (var fp in components.Select(c => c.Part.Footprint))
            foreach (var m in fp.Models.Where(m => !map.ContainsKey(m.Path)))
                if (resolver.Resolve(m) is { File: { } f }) map[m.Path] = f;

        var text = await File.ReadAllTextAsync(boardPath, ct);
        var patched = Regex.Replace(text, "\\(model \"([^\"]*)\"", m =>
            map.TryGetValue(m.Groups[1].Value, out var file) ? $"(model \"{file.Replace("\\", "/")}\"" : m.Value);
        return (patched, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patched)))[..20]);
    }

    /// <summary>The board text without any 3D models (bare board: copper, mask, silkscreen).</summary>
    internal static string WithoutModels(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (true)
        {
            var j = text.IndexOf("(model \"", i, StringComparison.Ordinal);
            if (j < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, j - i);
            var depth = 0; var k = j;
            for (; k < text.Length; k++)
            {
                if (text[k] == '(') depth++;
                else if (text[k] == ')' && --depth == 0) break;
            }
            i = k + 1;
        }
        return sb.ToString();
    }

    /// <summary>Runs kicad-cli on a temporary copy of the board text, in the cache folder.</summary>
    internal static async Task RunKiCadAsync(string cli, string boardText, string key, string output, IEnumerable<string> args, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var tmp = Path.Combine(CacheDir, $"{key}-{Guid.NewGuid():N}.kicad_pcb");
        await File.WriteAllTextAsync(tmp, boardText, ct);
        try
        {
            var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(output); psi.ArgumentList.Add(tmp);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("kicad-cli did not start");
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !File.Exists(output))
                throw new InvalidOperationException((await err).Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !l.StartsWith("Fontconfig")) ?? $"exit {p.ExitCode}");
        }
        finally
        {
            foreach (var extra in new[] { ".kicad_pcb", ".kicad_prl", ".kicad_pro" })
                try { File.Delete(Path.ChangeExtension(tmp, extra)); } catch (IOException) { }
        }
    }

    private static async Task<string> ExportGlbAsync(string cli, string boardPath, IReadOnlyList<ComponentInfo> components, Action<string>? progress, CancellationToken ct)
    {
        var (patched, key) = await PatchedBoardAsync(boardPath, components, ct);
        var glb = Path.Combine(CacheDir, $"{Path.GetFileNameWithoutExtension(boardPath)}-{key}.glb");
        if (File.Exists(glb) && new FileInfo(glb).Length > 0) return glb;
        progress?.Invoke("Building the 3D model with KiCad…");
        await RunKiCadAsync(cli, patched, key, glb, new[] { "pcb", "export", "glb", "--subst-models", "--include-tracks", "--include-pads",
            "--include-zones", "--include-silkscreen", "--include-soldermask" }, ct);
        return glb;
    }

    /// <summary>Without KiCad: the board as a green slab and every part as a box at its real height.</summary>
    public static Mesh3D Boxes(Circuit circuit, IReadOnlyList<ComponentInfo> components, string source)
    {
        var b = new MeshBuilder();
        var board = circuit.Board;
        var bounds = board.Bounds();
        var t = (float)board.Thickness;
        b.Box(new Vector3((float)bounds.MinX, (float)-bounds.MaxY, -t), new Vector3((float)bounds.MaxX, (float)-bounds.MinY, 0), new Vector3(0.05f, 0.32f, 0.14f));
        foreach (var c in components)
        {
            var fp = c.Part.Footprint;
            var r = fp.Bounds();
            if (r.IsEmpty || c.Part.Kind is PartKind.MountingHole or PartKind.TestPoint) continue;
            var h = (float)(c.Model3D?.HeightMm ?? (c.Part.Kind == PartKind.Connector ? 6 : 1));
            var color = c.Part.Kind switch
            {
                PartKind.Ic or PartKind.Regulator => new Vector3(0.08f, 0.08f, 0.09f),
                PartKind.Connector => new Vector3(0.9f, 0.9f, 0.86f),
                PartKind.Capacitor => new Vector3(0.62f, 0.5f, 0.33f),
                PartKind.Led => new Vector3(0.9f, 0.9f, 0.9f),
                _ => new Vector3(0.12f, 0.12f, 0.12f),
            };
            if (fp.Side == Side.Back) b.Box(new Vector3((float)r.MinX, (float)-r.MaxY, -t - h), new Vector3((float)r.MaxX, (float)-r.MinY, -t), color);
            else b.Box(new Vector3((float)r.MinX, (float)-r.MaxY, 0), new Vector3((float)r.MaxX, (float)-r.MinY, h), color);
        }
        return b.Build(source);
    }
}

internal sealed class MeshBuilder
{
    private readonly List<float> _p = new(), _n = new(), _c = new();
    private readonly List<uint> _i = new();

    public void Box(Vector3 lo, Vector3 hi, Vector3 color)
    {
        void Face(Vector3 n, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var start = (uint)(_p.Count / 3);
            foreach (var v in new[] { a, b, c, d }) { _p.AddRange(new[] { v.X, v.Y, v.Z }); _n.AddRange(new[] { n.X, n.Y, n.Z }); _c.AddRange(new[] { color.X, color.Y, color.Z }); }
            _i.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }
        Vector3 V(float x, float y, float z) => new(x, y, z);
        Face(Vector3.UnitZ, V(lo.X, lo.Y, hi.Z), V(hi.X, lo.Y, hi.Z), V(hi.X, hi.Y, hi.Z), V(lo.X, hi.Y, hi.Z));
        Face(-Vector3.UnitZ, V(lo.X, lo.Y, lo.Z), V(lo.X, hi.Y, lo.Z), V(hi.X, hi.Y, lo.Z), V(hi.X, lo.Y, lo.Z));
        Face(-Vector3.UnitY, V(lo.X, lo.Y, lo.Z), V(hi.X, lo.Y, lo.Z), V(hi.X, lo.Y, hi.Z), V(lo.X, lo.Y, hi.Z));
        Face(Vector3.UnitY, V(lo.X, hi.Y, lo.Z), V(lo.X, hi.Y, hi.Z), V(hi.X, hi.Y, hi.Z), V(hi.X, hi.Y, lo.Z));
        Face(-Vector3.UnitX, V(lo.X, lo.Y, lo.Z), V(lo.X, lo.Y, hi.Z), V(lo.X, hi.Y, hi.Z), V(lo.X, hi.Y, lo.Z));
        Face(Vector3.UnitX, V(hi.X, lo.Y, lo.Z), V(hi.X, hi.Y, lo.Z), V(hi.X, hi.Y, hi.Z), V(hi.X, lo.Y, hi.Z));
    }

    public Mesh3D Build(string source)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (var i = 0; i < _p.Count; i += 3) { var v = new Vector3(_p[i], _p[i + 1], _p[i + 2]); min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return new Mesh3D { Positions = _p.ToArray(), Normals = _n.ToArray(), Colors = _c.ToArray(), Indices = _i.ToArray(), Min = min, Max = max, Source = source };
    }
}

/// <summary>A minimal glTF 2.0 binary (.glb) reader: triangle meshes with normals and base colours, node transforms applied.</summary>
public static class Glb
{
    public static Mesh3D? Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67) throw new FormatException("Not a GLB file");
        var jsonLen = BitConverter.ToInt32(bytes, 12);
        using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 20, jsonLen));
        var binStart = 20 + jsonLen + 8;
        var root = doc.RootElement;

        var p = new List<float>(); var n = new List<float>(); var c = new List<float>(); var idx = new List<uint>();
        // JsonElement indexing walks the array, so turn the big arrays into real ones first.
        static JsonElement[] Arr(JsonElement root, string name) => root.TryGetProperty(name, out var a) ? a.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
        var meshes = Arr(root, "meshes");
        var accessors = Arr(root, "accessors");
        var views = Arr(root, "bufferViews");
        var materials = Arr(root, "materials");
        var nodes = Arr(root, "nodes");

        // glTF is Y-up in metres; BoardSim uses Z-up millimetres (KiCad's board coordinates).
        var toBoard = new Matrix4x4(1000, 0, 0, 0, 0, 0, 1000, 0, 0, -1000, 0, 0, 0, 0, 0, 1);

        void Visit(int nodeIndex, Matrix4x4 parent)
        {
            var node = nodes[nodeIndex];
            var local = Matrix4x4.Identity;
            if (node.TryGetProperty("matrix", out var mat))
            {
                var f = mat.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                local = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            }
            else
            {
                var s = node.TryGetProperty("scale", out var sc) ? Vec(sc) : Vector3.One;
                var r = node.TryGetProperty("rotation", out var ro) ? Quat(ro) : Quaternion.Identity;
                var t = node.TryGetProperty("translation", out var tr) ? Vec(tr) : Vector3.Zero;
                local = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
            }
            var world = local * parent;
            if (node.TryGetProperty("mesh", out var meshIndex))
                foreach (var prim in meshes[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue; // triangles only
                    var attrs = prim.GetProperty("attributes");
                    var pos = ReadFloats(bytes, binStart, accessors, views, attrs.GetProperty("POSITION").GetInt32(), 3);
                    var nor = attrs.TryGetProperty("NORMAL", out var na) ? ReadFloats(bytes, binStart, accessors, views, na.GetInt32(), 3) : null;
                    var (color, lift) = MaterialColor(materials, prim);
                    var baseIndex = (uint)(p.Count / 3);
                    var full = world * toBoard;
                    Matrix4x4.Invert(full, out var inv);
                    var normalM = Matrix4x4.Transpose(inv);
                    for (var i = 0; i < pos.Length; i += 3)
                    {
                        var v = Vector3.Transform(new Vector3(pos[i], pos[i + 1], pos[i + 2]), full);
                        var nv = nor is null ? Vector3.UnitZ : Vector3.Normalize(Vector3.TransformNormal(new Vector3(nor[i], nor[i + 1], nor[i + 2]), normalM));
                        // KiCad stacks copper, mask and silkscreen 0.01 mm apart; lift mask and silk off the copper so depth testing can tell them apart.
                        if (lift != 0) v += nv * lift;
                        p.Add(v.X); p.Add(v.Y); p.Add(v.Z);
                        n.Add(nv.X); n.Add(nv.Y); n.Add(nv.Z);
                        c.Add(color.X); c.Add(color.Y); c.Add(color.Z);
                    }
                    if (prim.TryGetProperty("indices", out var ia))
                        foreach (var k in ReadIndices(bytes, binStart, accessors, views, ia.GetInt32())) idx.Add(baseIndex + k);
                    else
                        for (uint k = 0; k < pos.Length / 3; k++) idx.Add(baseIndex + k);
                }
            if (node.TryGetProperty("children", out var children))
                foreach (var ch in children.EnumerateArray()) Visit(ch.GetInt32(), world);
        }

        var sceneIndex = root.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
        foreach (var nd in root.GetProperty("scenes")[sceneIndex].GetProperty("nodes").EnumerateArray()) Visit(nd.GetInt32(), Matrix4x4.Identity);
        if (p.Count == 0) return null;

        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (var i = 0; i < p.Count; i += 3) { var v = new Vector3(p[i], p[i + 1], p[i + 2]); min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return new Mesh3D { Positions = p.ToArray(), Normals = n.ToArray(), Colors = c.ToArray(), Indices = idx.ToArray(), Min = min, Max = max, Source = "KiCad (STEP models)" };
    }

    private static Vector3 Vec(JsonElement e) { var a = e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(); return new(a[0], a[1], a[2]); }
    private static Quaternion Quat(JsonElement e) { var a = e.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(); return new(a[0], a[1], a[2], a[3]); }

    /// <summary>
    /// The material's base colour, with KiCad's see-through solder mask made
    /// solid, and how far (mm) to lift the surface along its normal: mask and
    /// silkscreen sit just above the copper.
    /// </summary>
    private static (Vector3 Color, float Lift) MaterialColor(JsonElement[] materials, JsonElement prim)
    {
        var grey = (new Vector3(0.6f, 0.6f, 0.6f), 0f);
        if (materials.Length == 0 || !prim.TryGetProperty("material", out var mi)) return grey;
        var m = materials[mi.GetInt32()];
        if (!m.TryGetProperty("pbrMetallicRoughness", out var pbr) || !pbr.TryGetProperty("baseColorFactor", out var bc)) return grey;
        var f = bc.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        var alpha = f.Length > 3 ? f[3] : 1;
        if (alpha is > 0.8f and < 0.86f) return (new(0.03f, 0.22f, 0.09f), 0.03f);   // solder mask
        if (alpha is >= 0.86f and < 0.95f) return (new(f[0], f[1], f[2]), 0.06f);    // silkscreen
        return (new(f[0], f[1], f[2]), 0f);
    }

    private static (int Offset, int Stride, int Count, int Type) View(JsonElement[] accessors, JsonElement[] views, int accessor, int components, int size)
    {
        var a = accessors[accessor];
        var v = views[a.GetProperty("bufferView").GetInt32()];
        var offset = (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
        var stride = v.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : components * size;
        return (offset, stride, a.GetProperty("count").GetInt32(), a.GetProperty("componentType").GetInt32());
    }

    private static float[] ReadFloats(byte[] bytes, int bin, JsonElement[] accessors, JsonElement[] views, int accessor, int components)
    {
        var (offset, stride, count, _) = View(accessors, views, accessor, components, 4);
        var result = new float[count * components];
        for (var i = 0; i < count; i++)
            for (var k = 0; k < components; k++)
                result[i * components + k] = BitConverter.ToSingle(bytes, bin + offset + i * stride + k * 4);
        return result;
    }

    private static IEnumerable<uint> ReadIndices(byte[] bytes, int bin, JsonElement[] accessors, JsonElement[] views, int accessor)
    {
        var a = accessors[accessor];
        var type = a.GetProperty("componentType").GetInt32();
        var size = type switch { 5121 => 1, 5123 => 2, _ => 4 };
        var (offset, stride, count, _) = View(accessors, views, accessor, 1, size);
        for (var i = 0; i < count; i++)
        {
            var at = bin + offset + i * stride;
            yield return size switch { 1 => bytes[at], 2 => BitConverter.ToUInt16(bytes, at), _ => BitConverter.ToUInt32(bytes, at) };
        }
    }
}
