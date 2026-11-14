using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BoardSim.Core.Model;

namespace BoardSim.Core.Parts;

public enum PinType
{
    Passive, Power, PowerOut, Ground, In, Out, Io, Analog, Rf, Nc,
}

/// <summary>What BoardSim knows about one pin of a part.</summary>
public sealed class PinModel
{
    public string Name { get; set; } = "";
    public PinType Type { get; set; } = PinType.Passive;
    /// <summary>Nominal voltage of a power or power-out pin.</summary>
    public double? Volts { get; set; }
    /// <summary>Allowed supply range [min, max] for a power pin.</summary>
    public double[]? Range { get; set; }
    /// <summary>Absolute maximum voltage the pin can take.</summary>
    public double? MaxVolts { get; set; }
    /// <summary>Input high threshold: a number in volts or "0.7*VCC" (relative to the part's supply).</summary>
    public string? Vih { get; set; }
    /// <summary>Strapping pin: the level it must read at reset, "high" or "low".</summary>
    public string? Strap { get; set; }
    /// <summary>A pull the chip has inside, "up" or "down".</summary>
    public string? InternalPull { get; set; }
    /// <summary>An external pull the pin needs, "up" or "down".</summary>
    public string? Pull { get; set; }
    /// <summary>Output that goes high-impedance (mux channels, tri-state buffers).</summary>
    public bool Tristate { get; set; }
    /// <summary>Capacitance from the pin's net to ground, in farads.</summary>
    public double? MinCap { get; set; }
    public double? MaxCap { get; set; }
    /// <summary>For power pins: whether a decoupling capacitor is expected next to it.</summary>
    public bool? Decouple { get; set; }
    /// <summary>Why the pin has its rules; shown with the finding.</summary>
    public string? Why { get; set; }
}

/// <summary>
/// Simulation behaviour. "analogMux": Common connects to Channels[Select] while
/// Enable is active. "buffer": Output follows Input while Enable is active.
/// "mcu": I/O pins are inputs (with internal pulls) unless driven.
/// </summary>
public sealed class BehaviorModel
{
    public string Type { get; set; } = "";
    public string? Common { get; set; }
    public List<string>? Select { get; set; }
    public List<string>? Channels { get; set; }
    public string? Enable { get; set; }
    /// <summary>"low" or "high".</summary>
    public string? EnableActive { get; set; }
    public double? OnResistance { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
}

public sealed class PartMatch
{
    /// <summary>Regexes tried against the footprint's value.</summary>
    public List<string> Value { get; set; } = new();
    /// <summary>Regexes tried against the footprint library id.</summary>
    public List<string> Footprint { get; set; } = new();
}

public sealed class PartModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"ic", "connector", "tvs", ...</summary>
    public string Kind { get; set; } = "ic";
    public string? Datasheet { get; set; }
    public PartMatch Match { get; set; } = new();
    /// <summary>Name of the power pin whose voltage sets the logic levels.</summary>
    public string? Supply { get; set; }
    /// <summary>Pin number to pin model.</summary>
    public Dictionary<string, PinModel> Pins { get; set; } = new();
    /// <summary>Pin name pairs a signal passes through (buffer A to Y, mux Z to Yn).</summary>
    public List<string[]> Through { get; set; } = new();
    /// <summary>The crystal frequency the chip is built for (on its XTAL pins), in Hz.</summary>
    public double? CrystalHz { get; set; }
    /// <summary>How the part acts in the simulation; null for parts that only get checked.</summary>
    public BehaviorModel? Behavior { get; set; }

    public bool Matches(Footprint fp)
    {
        static bool Any(List<string> patterns, string text) =>
            patterns.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase));
        if (Match.Value.Count == 0 && Match.Footprint.Count == 0) return false;
        return (Match.Value.Count == 0 || Any(Match.Value, fp.Value)) &&
               (Match.Footprint.Count == 0 || Any(Match.Footprint, fp.LibId));
    }

    public PinModel? Pin(string number) => Pins.GetValueOrDefault(number);

    public IEnumerable<(string Number, PinModel Pin)> PinsNamed(string name) =>
        Pins.Where(p => string.Equals(p.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Select(p => (p.Key, p.Value));
}

/// <summary>The part models BoardSim ships with, plus any found next to the board.</summary>
public sealed class PartLibrary
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public List<PartModel> Models { get; } = new();

    public static PartLibrary BuiltIn()
    {
        var lib = new PartLibrary();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json")).OrderBy(n => n))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            lib.Models.Add(Parse(stream, name));
        }
        return lib;
    }

    /// <summary>Adds every *.part.json in a folder; these take precedence over built-in models.</summary>
    public void AddFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder, "*.part.json"))
        {
            using var stream = File.OpenRead(file);
            Models.Insert(0, Parse(stream, file));
        }
    }

    public static PartModel Parse(Stream stream, string source)
    {
        var model = JsonSerializer.Deserialize<PartModel>(stream, Options)
                    ?? throw new FormatException($"Empty part model: {source}");
        if (model.Id.Length == 0) throw new FormatException($"Part model without id: {source}");
        return model;
    }

    public PartModel? Find(Footprint fp) => Models.FirstOrDefault(m => m.Matches(fp));
}
