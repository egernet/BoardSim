using BoardSim.Core.Analysis;
using BoardSim.Core.KiCad;

// boardsim check <board.kicad_pcb> [--all]   run the rules, exit 1 on errors
// boardsim parts <board.kicad_pcb>           list parts and the model each one matched
// boardsim pins  <board.kicad_pcb> <REF>...  list the pins of parts with their nets

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        BoardSim - check a KiCad board

          boardsim check <board.kicad_pcb> [--all]   run the rules (exit code 1 on errors)
          boardsim parts <board.kicad_pcb>           parts and the model each one matched
          boardsim pins  <board.kicad_pcb> <REF>...  pins of the given parts and their nets

        Expected connections are read from <board>.boardsim.json next to the board.
        Extra part models (*.part.json) are read from a boardsim-parts folder next to it.
        """);
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
var path = args[1];
if (!File.Exists(path)) { Console.Error.WriteLine($"No such file: {path}"); return 2; }

switch (command)
{
    case "check":
    {
        var report = Checker.Run(path);
        var all = args.Contains("--all");
        Console.WriteLine($"{report.Board.Title}: {report.Board.Footprints.Count} parts, {report.Circuit.Nets.Count} nets" +
                          (report.Expectations is null ? "" : $", {report.Expectations.Connections.Count} expected connections"));
        Console.WriteLine();
        foreach (var f in report.Findings.Where(f => all || f.Severity != Severity.Info))
        {
            var color = f.Severity switch { Severity.Error => ConsoleColor.Red, Severity.Warning => ConsoleColor.Yellow, _ => ConsoleColor.Gray };
            Console.ForegroundColor = color;
            Console.Write($"{f.Severity.ToString().ToUpperInvariant(),-8}");
            Console.ResetColor();
            Console.WriteLine($"{f.Title}  [{f.Rule}]");
            if (f.Detail.Length > 0) Console.WriteLine($"        {f.Detail}");
        }
        var infos = report.Findings.Count(f => f.Severity == Severity.Info);
        Console.WriteLine();
        Console.WriteLine($"{report.Errors} errors, {report.Warnings} warnings, {infos} notes" + (all || infos == 0 ? "" : " (--all shows the notes)"));
        return report.Errors > 0 ? 1 : 0;
    }
    case "parts":
    {
        var report = Checker.Run(path);
        foreach (var p in report.Circuit.Parts.OrderBy(p => p.Footprint.Prefix).ThenBy(p => p.Reference.Length).ThenBy(p => p.Reference))
            Console.WriteLine($"{p.Reference,-7} {p.Footprint.Value,-26} {p.Kind,-12} {p.Model?.Id ?? "-"}");
        return 0;
    }
    case "pins":
    {
        var report = Checker.Run(path);
        foreach (var r in args.Skip(2))
        {
            var part = report.Circuit.Part(r);
            if (part is null) { Console.Error.WriteLine($"No part {r}"); continue; }
            Console.WriteLine($"{part.Reference} {part.Footprint.Value} ({part.Model?.Name ?? "no model"})");
            foreach (var pin in part.Pins.OrderBy(p => p.Number.Length).ThenBy(p => p.Number))
                Console.WriteLine($"  {pin.Number,-4} {pin.Name,-12} {pin.Model?.Type.ToString() ?? "",-9} {pin.Net.DisplayName}" +
                                  (pin.Net.Volts is { } v ? $"  ({v} V)" : ""));
        }
        return 0;
    }
    default:
        Console.Error.WriteLine($"Unknown command {command}");
        return 2;
}
