using System.Globalization;
using System.Text;

namespace BoardSim.Core.KiCad;

/// <summary>
/// One node of a KiCad s-expression file: either an atom (symbol, number or
/// quoted string) or a list whose first item is usually a keyword.
/// </summary>
public sealed class SExpr
{
    public string? Atom { get; }
    public IReadOnlyList<SExpr> Items { get; }
    public bool IsList => Atom is null;

    private SExpr(string atom) { Atom = atom; Items = Array.Empty<SExpr>(); }
    private SExpr(List<SExpr> items) { Items = items; }

    /// <summary>The keyword of a list, e.g. "footprint" for (footprint ...).</summary>
    public string Name => IsList && Items.Count > 0 && Items[0].Atom is { } a ? a : "";

    public IEnumerable<SExpr> Children(string name) =>
        Items.Where(i => i.IsList && i.Name == name);

    public SExpr? Child(string name) => Children(name).FirstOrDefault();

    /// <summary>The atom at position <paramref name="index"/> (0 is the keyword).</summary>
    public string? Arg(int index) => index < Items.Count ? Items[index].Atom : null;

    public double Num(int index, double fallback = 0) =>
        Arg(index) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    /// <summary>All atoms after the keyword, e.g. the layer names of (layers "F.Cu" "F.Mask").</summary>
    public IEnumerable<string> Atoms() => Items.Skip(1).Where(i => !i.IsList).Select(i => i.Atom!);

    /// <summary>True for (hide yes), (hide) and a bare "hide" atom, all used by KiCad versions.</summary>
    public bool Flag(string name)
    {
        foreach (var item in Items.Skip(1))
        {
            if (!item.IsList && item.Atom == name) return true;
            if (item.IsList && item.Name == name) return item.Arg(1) is null or "yes";
        }
        return false;
    }

    public static SExpr Parse(string text)
    {
        var pos = 0;
        SkipSpace(text, ref pos);
        var root = ReadNode(text, ref pos);
        return root;
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    private static SExpr ReadNode(string s, ref int pos)
    {
        if (pos >= s.Length) throw new FormatException("Unexpected end of file");
        if (s[pos] == '(')
        {
            pos++;
            var items = new List<SExpr>();
            while (true)
            {
                SkipSpace(s, ref pos);
                if (pos >= s.Length) throw new FormatException("Unclosed list");
                if (s[pos] == ')') { pos++; break; }
                items.Add(ReadNode(s, ref pos));
            }
            return new SExpr(items);
        }
        if (s[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    pos++;
                    sb.Append(s[pos] switch { 'n' => '\n', 't' => '\t', _ => s[pos] });
                }
                else sb.Append(s[pos]);
                pos++;
            }
            pos++; // closing quote
            return new SExpr(sb.ToString());
        }
        var start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] != '(' && s[pos] != ')') pos++;
        return new SExpr(s[start..pos]);
    }

    public override string ToString() => IsList ? $"({Name} …)" : Atom!;
}
