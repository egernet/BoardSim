using System.Globalization;
using System.Text.RegularExpressions;
using BoardSim.Core.Analysis;
using BoardSim.Core.Model;

namespace BoardSim.Core.Components;

/// <summary>Where a piece of component data came from.</summary>
public enum DataSource
{
    /// <summary>A file in the board's own project folder.</summary>
    Project,
    /// <summary>KiCad's standard library.</summary>
    KiCadLibrary,
    /// <summary>A third-party library installed through KiCad's plugin manager.</summary>
    ThirdParty,
    /// <summary>Stored inside the board file (KiCad 9+, kicad-embed://); KiCad reads it from there.</summary>
    Embedded,
    /// <summary>The footprint names a file that could not be found.</summary>
    Missing,
}

/// <summary>A footprint's 3D model, found on disk (or not), with the height it reaches above the board.</summary>
public sealed record ResolvedModel(Model3D Model, string? File, DataSource Source, double? HeightMm)
{
    public string FileName => System.IO.Path.GetFileName(Model.Path.Replace('\\', '/'));
}

/// <summary>What BoardSim uses for one part: its electrical model and its 3D model.</summary>
public sealed record ComponentInfo(Part Part, string? PartModel, bool PartModelFromProject, ResolvedModel? Model3D, string? Mpn)
{
    public bool IsGeneric => PartModel is null;
}

/// <summary>
/// Finds the real data for every part: the part model that matched it, and the
/// 3D model file the footprint names. Paths are resolved the way KiCad does
/// (${KIPRJMOD}, ${KICADn_3DMODEL_DIR}, ${KICADn_3RD_PARTY}), and when that
/// fails the file is looked up by name in the project folder, so boards made
/// with older KiCad versions still find the models that sit next to them.
/// </summary>
public static class ComponentData
{
    public static IReadOnlyList<ComponentInfo> For(Circuit circuit, string? projectDir = null)
    {
        projectDir ??= Path.GetDirectoryName(Path.GetFullPath(circuit.Board.FilePath.Length > 0 ? circuit.Board.FilePath : "."));
        var resolver = new ModelResolver(projectDir!);
        return circuit.Parts.Select(p =>
        {
            var model = p.Footprint.Models.FirstOrDefault(m => !m.Hidden) ?? p.Footprint.Models.FirstOrDefault();
            var mpn = new[] { "MPN", "Manufacturer Part Number", "MFR", "LCSC", "LCSC Part" }
                .Select(k => p.Footprint.Properties.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return new ComponentInfo(p, p.Model?.Id, p.Model?.Source?.StartsWith("project:") ?? false,
                model is null ? null : resolver.Resolve(model), mpn);
        }).ToList();
    }
}

public sealed class ModelResolver
{
    private readonly string _project;
    private Dictionary<string, string>? _projectIndex;
    private static Dictionary<string, string>? _libraryIndex;
    private static readonly Dictionary<string, double?> HeightCache = new();
    private static readonly string[] Extensions = { ".step", ".stp", ".STEP", ".STP", ".wrl", ".WRL" };

    public ModelResolver(string projectDir) => _project = projectDir;

    public ResolvedModel Resolve(Model3D model)
    {
        var (file, source) = Find(model.Path);
        return new ResolvedModel(model, file, source, file is null ? null : Height(file, model));
    }

    private (string? File, DataSource Source) Find(string raw)
    {
        var path = raw.Replace('\\', '/');
        if (path.StartsWith("kicad-embed://", StringComparison.OrdinalIgnoreCase)) return (null, DataSource.Embedded);
        var m = Regex.Match(path, @"^\$\{(\w+)\}/?(.*)$");
        if (m.Success)
        {
            var (variable, rest) = (m.Groups[1].Value, m.Groups[2].Value);
            if (variable == "KIPRJMOD")
            {
                if (Existing(Path.Combine(_project, rest)) is { } f) return (f, DataSource.Project);
            }
            else if (variable.EndsWith("3DMODEL_DIR"))
            {
                foreach (var dir in LibraryDirs(variable))
                    if (Existing(Path.Combine(dir, rest)) is { } f) return (f, DataSource.KiCadLibrary);
            }
            else if (variable.EndsWith("3RD_PARTY"))
            {
                foreach (var dir in ThirdPartyDirs())
                    if (Existing(Path.Combine(dir, rest)) is { } f) return (f, DataSource.ThirdParty);
            }
        }
        else if (Path.IsPathRooted(path) && Existing(path) is { } abs)
            return (abs, abs.StartsWith(_project, StringComparison.OrdinalIgnoreCase) ? DataSource.Project : DataSource.ThirdParty);
        else if (!Path.IsPathRooted(path) && Existing(Path.Combine(_project, path)) is { } rel)
            return (rel, DataSource.Project);

        // Not where the footprint says: look for the file by name, first in the project.
        var name = Normalize(Path.GetFileNameWithoutExtension(path));
        if (ProjectIndex().TryGetValue(name, out var local)) return (local, DataSource.Project);
        if (LibraryIndex().TryGetValue(name, out var lib)) return (lib, DataSource.KiCadLibrary);
        return (null, DataSource.Missing);
    }

    /// <summary>The file, or the same file with another 3D extension (KiCad 10 ships .step where older footprints say .wrl).</summary>
    private static string? Existing(string path)
    {
        if (File.Exists(path)) return path;
        var stem = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
        return Extensions.Select(e => stem + e).FirstOrDefault(File.Exists);
    }

    private static string Normalize(string name) => Regex.Replace(name.ToLowerInvariant(), @"[\s_\-.]+", "");

    /// <summary>
    /// Model files in the project, by name. Boards often sit in a sub-folder
    /// (Hardware/) with the models next to it (Libraries/3D models), so the
    /// search starts at the repository root, or one folder up when there is none.
    /// </summary>
    private Dictionary<string, string> ProjectIndex() => _projectIndex ??= Index(ProjectRoots(_project), depth: 4);

    public static IReadOnlyList<string> ProjectRoots(string boardDir)
    {
        var roots = new List<string> { boardDir };
        var dir = new DirectoryInfo(boardDir);
        for (var up = 0; up < 3 && dir is not null; up++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                if (dir.FullName != boardDir) roots.Add(dir.FullName);
                return roots;
            }
        var parent = Directory.GetParent(boardDir)?.FullName;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (parent is not null && parent != home && Directory.GetParent(parent) is not null) roots.Add(parent);
        return roots;
    }

    private static Dictionary<string, string> LibraryIndex() =>
        _libraryIndex ??= Index(LibraryDirs("").Concat(ThirdPartyDirs()), depth: 4);

    private static Dictionary<string, string> Index(IEnumerable<string> roots, int depth)
    {
        var index = new Dictionary<string, string>();
        void Walk(string dir, int left)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (Extensions.Contains(Path.GetExtension(f)))
                    {
                        var key = Normalize(Path.GetFileNameWithoutExtension(f));
                        // Prefer STEP over VRML: it has real dimensions.
                        if (!index.TryGetValue(key, out var have) || have.EndsWith(".wrl", StringComparison.OrdinalIgnoreCase))
                            index[key] = f;
                    }
                if (left > 0)
                    foreach (var d in Directory.EnumerateDirectories(dir))
                        if (!Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) is not ("node_modules" or "bin" or "obj" or "build")
                            && !Path.GetFileName(d).EndsWith("-backups"))
                            Walk(d, left - 1);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        }
        // The first file found for a name stays (unless it is VRML), so a model next to the board beats one elsewhere in the repository.
        foreach (var r in roots.Where(Directory.Exists)) Walk(r, depth);
        return index;
    }

    /// <summary>KiCad's 3D library folders, newest install first.</summary>
    public static IEnumerable<string> LibraryDirs(string variable)
    {
        if (variable.Length > 0 && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env) yield return env;
        foreach (var v in Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.EndsWith("3DMODEL_DIR")).OrderDescending())
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } e) yield return e;
        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/KiCad/KiCad.app/Contents/SharedSupport/3dmodels";
            foreach (var app in SafeDirs("/Applications", "KiCad*").OrderDescending())
                yield return Path.Combine(app, "KiCad.app/Contents/SharedSupport/3dmodels");
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"C:\Program Files" }.Distinct())
                foreach (var ver in SafeDirs(Path.Combine(pf, "KiCad"), "*").OrderDescending())
                    yield return Path.Combine(ver, "share", "kicad", "3dmodels");
        }
        else
        {
            yield return "/usr/share/kicad/3dmodels";
            yield return "/usr/local/share/kicad/3dmodels";
        }
    }

    public static IEnumerable<string> ThirdPartyDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var root in new[] { Path.Combine(docs, "KiCad"), Path.Combine(home, "Documents", "KiCad"), Path.Combine(home, ".local", "share", "kicad") }.Distinct())
            foreach (var ver in SafeDirs(root, "*").OrderDescending())
            {
                yield return Path.Combine(ver, "3rdparty", "3dmodels");
                yield return Path.Combine(ver, "3dmodels");
            }
    }

    private static IEnumerable<string> SafeDirs(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>How far the model reaches above the board, in mm (STEP in mm or m, KiCad VRML in 0.1 inch).</summary>
    public static double? Height(string file, Model3D model)
    {
        double? top;
        lock (HeightCache)
            if (!HeightCache.TryGetValue(file, out top))
                HeightCache[file] = top = ReadTop(file);
        return top is { } t ? Math.Round(t * model.Scale[2] + model.Offset[2], 2) : null;
    }

    private static readonly Regex StepPoint = new(@"CARTESIAN_POINT\s*\(\s*'[^']*'\s*,\s*\(\s*([-+\d.Ee]+)\s*,\s*([-+\d.Ee]+)\s*,\s*([-+\d.Ee]+)", RegexOptions.Compiled);

    private static double? ReadTop(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".wrl", StringComparison.OrdinalIgnoreCase))
            {
                double max = double.MinValue;
                foreach (Match m in Regex.Matches(text, @"point\s*\[([^\]]*)\]"))
                {
                    var nums = m.Groups[1].Value.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 2; i < nums.Length; i += 3)
                        if (double.TryParse(nums[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) max = Math.Max(max, z);
                }
                return max == double.MinValue ? null : max * 2.54;
            }
            var metres = Regex.IsMatch(text, @"SI_UNIT\s*\(\s*\$\s*,\s*\.METRE\.\s*\)") && !Regex.IsMatch(text, @"\.MILLI\.\s*,\s*\.METRE\.");
            var top = double.MinValue;
            foreach (Match m in StepPoint.Matches(text))
                if (double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) top = Math.Max(top, z);
            return top == double.MinValue ? null : metres ? top * 1000 : top;
        }
        catch (Exception) { return null; }
    }
}
