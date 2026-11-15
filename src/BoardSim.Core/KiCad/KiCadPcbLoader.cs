using System.Globalization;
using BoardSim.Core.Model;

namespace BoardSim.Core.KiCad;

/// <summary>
/// Reads a .kicad_pcb file (KiCad 6 to 10). Pad nets are the netlist: KiCad
/// writes the schematic's net name onto every pad when the board is updated.
/// </summary>
public static class KiCadPcbLoader
{
    public static Board Load(string path) => Parse(File.ReadAllText(path), path);

    public static Board Parse(string text, string path = "")
    {
        var root = SExpr.Parse(text);
        if (root.Name != "kicad_pcb")
            throw new FormatException("Not a KiCad board file (expected kicad_pcb)");

        var board = new Board
        {
            FilePath = path,
            Version = root.Child("version")?.Arg(1) ?? "",
            Generator = root.Child("generator")?.Arg(1) ?? "",
            Thickness = root.Child("general")?.Child("thickness")?.Num(1, 1.6) ?? 1.6,
        };

        foreach (var l in root.Child("layers")?.Items.Skip(1).Where(i => i.IsList) ?? Enumerable.Empty<SExpr>())
        {
            board.Layers.Add(new LayerInfo
            {
                Ordinal = int.TryParse(l.Name, out var n) ? n : 0,
                Name = l.Arg(1) ?? "",
                Type = l.Arg(2) ?? "",
            });
        }

        foreach (var l in root.Child("setup")?.Child("stackup")?.Children("layer") ?? Enumerable.Empty<SExpr>())
        {
            var type = l.Child("type")?.Arg(1) ?? "";
            if (type is "Top Silk Screen" or "Bottom Silk Screen" or "Top Solder Paste" or "Bottom Solder Paste") continue;
            board.Stackup.Add(new StackLayer(l.Arg(1) ?? "", type, l.Child("thickness")?.Num(1) ?? 0,
                l.Child("epsilon_r")?.Num(1, 4.5) ?? 4.5));
        }

        // KiCad 6/7 number the nets and refer to them by number on tracks and vias.
        var netNames = new Dictionary<string, string>();
        foreach (var n in root.Children("net"))
            if (n.Arg(1) is { } id && n.Arg(2) is { } name) netNames[id] = name;

        var ctx = new Context(board, netNames);

        foreach (var item in root.Items.Where(i => i.IsList))
        {
            switch (item.Name)
            {
                case "footprint":
                case "module":
                    board.Footprints.Add(ReadFootprint(item, ctx));
                    break;
                case "segment":
                    board.Tracks.Add(new Track(Xy(item.Child("start")), Xy(item.Child("end")),
                        item.Child("width")?.Num(1) ?? 0.2, item.Child("layer")?.Arg(1) ?? "", ctx.Net(item)));
                    break;
                case "arc":
                    board.Tracks.Add(new Track(Xy(item.Child("start")), Xy(item.Child("end")),
                        item.Child("width")?.Num(1) ?? 0.2, item.Child("layer")?.Arg(1) ?? "", ctx.Net(item),
                        Xy(item.Child("mid"))));
                    break;
                case "via":
                    board.Vias.Add(new Via(Xy(item.Child("at")), item.Child("size")?.Num(1) ?? 0.6,
                        item.Child("drill")?.Num(1) ?? 0.3, ctx.ExpandLayers(item.Child("layers")?.Atoms() ?? new[] { "F.Cu", "B.Cu" }, isVia: true),
                        ctx.Net(item)));
                    break;
                case "zone":
                    board.Zones.Add(ReadZone(item, ctx));
                    break;
                default:
                    if (item.Name.StartsWith("gr_") && ReadShape(item, null, ctx) is { } shape)
                        board.Graphics.Add(shape);
                    break;
            }
        }

        return board;
    }

    private sealed class Context(Board board, Dictionary<string, string> netNames)
    {
        public Board Board { get; } = board;

        public string Net(SExpr node)
        {
            if (node.Child("net_name")?.Arg(1) is { } named) return named;
            var net = node.Child("net");
            if (net is null) return "";
            var atoms = net.Atoms().ToList();
            if (atoms.Count >= 2) return atoms[^1];                      // (net 3 "GND") on KiCad 6/7 pads
            if (atoms.Count == 1)
                return netNames.Count > 0 && netNames.TryGetValue(atoms[0], out var n) ? n : atoms[0];
            return "";
        }

        public IReadOnlyList<string> ExpandLayers(IEnumerable<string> layers, bool isVia = false)
        {
            var copper = Board.CopperLayers;
            var list = layers.ToList();
            if (isVia && list.Count == 2 && list.All(l => l.EndsWith(".Cu")))
            {
                // A via spans every copper layer between its two ends.
                var a = IndexOf(copper, list[0]);
                var b = IndexOf(copper, list[1]);
                if (a >= 0 && b >= 0) return copper.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).ToList();
            }
            var result = new List<string>();
            foreach (var l in list)
            {
                if (l == "*.Cu") result.AddRange(copper);
                else if (l.StartsWith("*.")) { result.Add("F" + l[1..]); result.Add("B" + l[1..]); }
                else if (l.StartsWith("F&B.")) { result.Add("F" + l[3..]); result.Add("B" + l[3..]); }
                else result.Add(l);
            }
            return result.Distinct().ToList();
        }

        private static int IndexOf(IReadOnlyList<string> list, string item)
        {
            for (var i = 0; i < list.Count; i++) if (list[i] == item) return i;
            return -1;
        }
    }

    private static Vec2 Xy(SExpr? node) => node is null ? default : new Vec2(node.Num(1), node.Num(2));

    private static Footprint ReadFootprint(SExpr node, Context ctx)
    {
        var at = node.Child("at");
        var pos = Xy(at);
        var angle = at?.Num(3) ?? 0;
        var layer = node.Child("layer")?.Arg(1) ?? "F.Cu";
        var attr = node.Child("attr");

        string Prop(string name) =>
            node.Children("property").FirstOrDefault(p => string.Equals(p.Arg(1), name, StringComparison.OrdinalIgnoreCase))?.Arg(2)
            ?? node.Children("fp_text").FirstOrDefault(t => string.Equals(t.Arg(1), name, StringComparison.OrdinalIgnoreCase))?.Arg(2)
            ?? "";

        var fp = new Footprint
        {
            Reference = Prop("Reference") is { Length: > 0 } r ? r : "?",
            Value = Prop("Value"),
            LibId = node.Arg(1) ?? "",
            Description = node.Child("descr")?.Arg(1) ?? "",
            Position = pos,
            Angle = angle,
            Side = layer == "B.Cu" ? Side.Back : Side.Front,
            IsSmd = attr?.Atoms().Contains("smd") ?? false,
            DoNotPopulate = (attr?.Atoms().Contains("dnp") ?? false) || node.Flag("dnp"),
            ExcludeFromBom = (attr?.Atoms().Contains("exclude_from_bom") ?? false) || node.Flag("exclude_from_bom"),
        };

        foreach (var p in node.Children("property"))
            if (p.Arg(1) is { } k) fp.Properties[k] = p.Arg(2) ?? "";

        Vec2 ToBoard(Vec2 local) => pos + local.Rotate(angle);

        foreach (var item in node.Items.Where(i => i.IsList))
        {
            if (item.Name == "pad")
                fp.Pads.Add(ReadPad(item, fp, ToBoard, ctx));
            else if (item.Name.StartsWith("fp_") && item.Name != "fp_text" && ReadShape(item, ToBoard, ctx) is { } s)
                fp.Graphics.Add(s);
            else if ((item.Name == "fp_text" || item.Name == "property") && ReadFootprintText(item, ToBoard) is { } t)
                fp.Graphics.Add(t);
            else if (item.Name == "model" && item.Arg(1) is { Length: > 0 } modelPath)
            {
                static double[] V(SExpr? n, double d) => n?.Child("xyz") is { } x ? new[] { x.Num(1, d), x.Num(2, d), x.Num(3, d) } : new[] { d, d, d };
                fp.Models.Add(new Model3D(modelPath, V(item.Child("offset"), 0), V(item.Child("scale"), 1), V(item.Child("rotate"), 0),
                    item.Child("hide") is not null || item.Atoms().Contains("hide")));
            }
        }
        return fp;
    }

    private static TextShape? ReadFootprintText(SExpr node, Func<Vec2, Vec2> toBoard)
    {
        // (fp_text reference "R1" (at ...) (layer ...)) or (property "Reference" "R1" (at ...) (layer ...))
        var layer = node.Child("layer")?.Arg(1);
        if (layer is null) return null;
        var effects = node.Child("effects");
        if (node.Flag("hide") || (effects?.Flag("hide") ?? false)) return null;
        var text = node.Arg(2) ?? "";
        if (text.Length == 0) return null;
        if (node.Name == "property" && node.Arg(1) is not ("Reference" or "Value")) return null;
        if (node.Name == "fp_text" && node.Arg(1) == "user" && text.Contains("${")) text = text.Replace("${REFERENCE}", "");
        var at = node.Child("at");
        var font = effects?.Child("font");
        var height = font?.Child("size")?.Num(1, 1) ?? 1;
        var thick = font?.Child("thickness")?.Num(1, height * 0.15) ?? height * 0.15;
        var (mirrored, align, top, bottom) = Justify(effects);
        return new TextShape(layer, thick, text, toBoard(Xy(at)), at?.Num(3) ?? 0, height, mirrored, align, top, bottom);
    }

    private static Pad ReadPad(SExpr node, Footprint fp, Func<Vec2, Vec2> toBoard, Context ctx)
    {
        var at = node.Child("at");
        var local = Xy(at);
        var center = toBoard(local);
        var padAngle = at?.Num(3) ?? 0;
        var size = Xy(node.Child("size"));
        var drillNode = node.Child("drill");
        var drill = default(Vec2);
        if (drillNode is not null)
        {
            var nums = drillNode.Atoms().Where(a => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count == 1) drill = new Vec2(nums[0], nums[0]);
            else if (nums.Count >= 2) drill = new Vec2(nums[0], nums[1]);
        }

        var primitives = new List<IReadOnlyList<Vec2>>();
        foreach (var prim in node.Child("primitives")?.Items.Where(i => i.IsList) ?? Enumerable.Empty<SExpr>())
        {
            Vec2 PadToBoard(Vec2 p) => center + p.Rotate(padAngle);
            if (prim.Name == "gr_poly" && prim.Child("pts") is { } pts)
                primitives.Add(pts.Children("xy").Select(xy => PadToBoard(Xy(xy))).ToList());
            else if (prim.Name == "gr_rect")
            {
                var a = Xy(prim.Child("start"));
                var b = Xy(prim.Child("end"));
                primitives.Add(new[] { a, new Vec2(b.X, a.Y), b, new Vec2(a.X, b.Y) }.Select(PadToBoard).ToList());
            }
            else if (prim.Name == "gr_circle")
            {
                var c = Xy(prim.Child("center"));
                var r = c.DistanceTo(Xy(prim.Child("end")));
                primitives.Add(Enumerable.Range(0, 16).Select(i => PadToBoard(c + new Vec2(r * Math.Cos(i * Math.PI / 8), r * Math.Sin(i * Math.PI / 8)))).ToList());
            }
        }

        return new Pad
        {
            Footprint = fp,
            Number = node.Arg(1) ?? "",
            Kind = node.Arg(2) switch
            {
                "thru_hole" => PadKind.ThroughHole,
                "np_thru_hole" => PadKind.NpThroughHole,
                "connect" => PadKind.Connect,
                _ => PadKind.Smd,
            },
            Shape = node.Arg(3) switch
            {
                "circle" => PadShape.Circle,
                "oval" => PadShape.Oval,
                "roundrect" => PadShape.RoundRect,
                "trapezoid" => PadShape.Trapezoid,
                "custom" => PadShape.Custom,
                _ => PadShape.Rect,
            },
            Position = center,
            Size = size,
            Angle = padAngle,
            RoundRectRatio = node.Child("roundrect_rratio")?.Num(1) ?? 0,
            Drill = drill,
            Layers = ctx.ExpandLayers(node.Child("layers")?.Atoms() ?? Enumerable.Empty<string>()),
            Net = ctx.Net(node),
            PinFunction = node.Child("pinfunction")?.Arg(1) ?? "",
            Primitives = primitives,
        };
    }

    private static Zone ReadZone(SExpr node, Context ctx)
    {
        var layers = node.Child("layers")?.Atoms().ToList()
                     ?? (node.Child("layer")?.Arg(1) is { } l ? new List<string> { l } : new List<string>());
        var zone = new Zone
        {
            Net = ctx.Net(node),
            Layers = ctx.ExpandLayers(layers),
            IsRuleArea = node.Child("keepout") is not null,
            Outline = node.Child("polygon")?.Child("pts")?.Children("xy").Select(Xy).ToList() ?? new List<Vec2>(),
        };
        foreach (var fill in node.Children("filled_polygon"))
        {
            var layer = fill.Child("layer")?.Arg(1) ?? zone.Layers.FirstOrDefault() ?? "";
            var pts = fill.Child("pts")?.Children("xy").Select(Xy).ToList();
            if (pts is { Count: > 2 }) zone.Fills.Add((layer, pts));
        }
        return zone;
    }

    private static Shape? ReadShape(SExpr node, Func<Vec2, Vec2>? toBoard, Context ctx)
    {
        Vec2 T(Vec2 p) => toBoard is null ? p : toBoard(p);
        var layer = node.Child("layer")?.Arg(1) ?? "";
        var width = node.Child("stroke")?.Child("width")?.Num(1) ?? node.Child("width")?.Num(1) ?? 0.1;
        var fill = node.Child("fill");
        var filled = fill is not null && (fill.Arg(1) is "solid" or "yes");
        var kind = node.Name[3..]; // drop "gr_" / "fp_"

        switch (kind)
        {
            case "line":
                return new LineShape(layer, width, T(Xy(node.Child("start"))), T(Xy(node.Child("end"))));
            case "arc":
                if (node.Child("mid") is { } mid)
                    return new ArcShape(layer, width, T(Xy(node.Child("start"))), T(Xy(mid)), T(Xy(node.Child("end"))));
                return LegacyArc(node, layer, width, T);
            case "circle":
            {
                var c = Xy(node.Child("center"));
                var e = Xy(node.Child("end"));
                return new CircleShape(layer, width, T(c), c.DistanceTo(e), filled);
            }
            case "rect":
            {
                var a = Xy(node.Child("start"));
                var b = Xy(node.Child("end"));
                return new PolyShape(layer, width,
                    new[] { a, new Vec2(b.X, a.Y), b, new Vec2(a.X, b.Y) }.Select(T).ToList(), filled);
            }
            case "poly":
                return new PolyShape(layer, width,
                    node.Child("pts")?.Children("xy").Select(xy => T(Xy(xy))).ToList() ?? new List<Vec2>(), filled);
            case "curve":
            {
                var pts = node.Child("pts")?.Children("xy").Select(xy => T(Xy(xy))).ToList() ?? new List<Vec2>();
                return pts.Count >= 2 ? new PolyShape(layer, width, Bezier(pts), false) : null;
            }
            case "text":
            {
                if (node.Flag("hide") || (node.Child("effects")?.Flag("hide") ?? false)) return null;
                var at = node.Child("at");
                var font = node.Child("effects")?.Child("font");
                var h = font?.Child("size")?.Num(1, 1) ?? 1;
                var (mirrored, align, top, bottom) = Justify(node.Child("effects"));
                return new TextShape(layer, font?.Child("thickness")?.Num(1, h * 0.15) ?? h * 0.15,
                    node.Arg(1) ?? "", T(Xy(at)), at?.Num(3) ?? 0, h, mirrored, align, top, bottom);
            }
        }
        return null;
    }

    private static (bool Mirror, TextAlign Align, bool Top, bool Bottom) Justify(SExpr? effects)
    {
        var j = effects?.Child("justify")?.Atoms().ToList() ?? new List<string>();
        var align = j.Contains("left") ? TextAlign.Left : j.Contains("right") ? TextAlign.Right : TextAlign.Center;
        return (j.Contains("mirror"), align, j.Contains("top"), j.Contains("bottom"));
    }

    private static Shape LegacyArc(SExpr node, string layer, double width, Func<Vec2, Vec2> t)
    {
        // KiCad 5: (start = centre) (end = start point) (angle degrees)
        var c = Xy(node.Child("start"));
        var s = Xy(node.Child("end"));
        var ang = node.Child("angle")?.Num(1) ?? 90;
        var m = c + (s - c).Rotate(-ang / 2);
        var e = c + (s - c).Rotate(-ang);
        return new ArcShape(layer, width, t(s), t(m), t(e));
    }

    private static List<Vec2> Bezier(List<Vec2> p)
    {
        if (p.Count != 4) return p;
        var result = new List<Vec2>();
        for (var i = 0; i <= 16; i++)
        {
            var t = i / 16.0;
            var u = 1 - t;
            result.Add(p[0] * (u * u * u) + p[1] * (3 * u * u * t) + p[2] * (3 * u * t * t) + p[3] * (t * t * t));
        }
        return result;
    }
}
