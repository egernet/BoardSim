using BoardSim.Core.Analysis;
using BoardSim.Core.Emulation;
using BoardSim.Core.KiCad;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

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
          boardsim chip  <board.kicad_pcb> [--port 4141] [--flash build-folder] [--lan]
                                                     power the board's ESP32-C3 in QEMU and serve its
                                                     USB port: idf.py -p rfc2217://localhost:4141 flash
                                                     and its pins on localhost:4142 (--lan: on the network)
          boardsim gpio  <board.kicad_pcb> [--lan]  serve the simulated board's pins on port 4142

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
    case "chip":
    {
        var report = Checker.Run(path);
        var mcu = EmulatedBoard.FindMcu(report.Circuit);
        if (mcu is null) { Console.Error.WriteLine("No ESP32-C3 on this board."); return 2; }
        if (Firmware.FindQemu() is not { } qemu) { Console.Error.WriteLine("BoardSim's QEMU not found; set BOARDSIM_QEMU."); return 2; }
        var portArg = Array.IndexOf(args, "--port");
        var port = portArg > 0 ? int.Parse(args[portArg + 1]) : 4141;
        var flashArg = Array.IndexOf(args, "--flash");
        var sim = new Simulator(report.Circuit);
        var board = new EmulatedBoard(sim, mcu, report.Expectations?.LedOrder ?? "GRB");
        using var chip = new EmulatedChip(board, qemu, EmulatedChip.DefaultFlashPath(path), port);
        chip.TraceControlLines = args.Contains("--trace");
        chip.Console += text => Console.Write(text);
        chip.Status += line => Console.Error.WriteLine($"[boardsim] {line}");
        chip.Exited += code => Console.Error.WriteLine($"[boardsim] QEMU exited ({code}): {chip.Log.Trim().Split('\n').LastOrDefault()}");
        if (flashArg > 0) chip.LoadBuild(args[flashArg + 1]); else chip.PowerOn();
        Console.Error.WriteLine($"[boardsim] {mcu.Reference} powered, flash {chip.FlashPath}{(chip.FlashIsBlank() ? " (blank)" : "")}");
        Console.Error.WriteLine($"[boardsim] USB port: {chip.Port.Url}   e.g.  idf.py -p {chip.Port.Url} flash monitor");
        if (chip.ActiveStreamPort is { } sp) Console.Error.WriteLine($"[boardsim] UDP 127.0.0.1:{sp} is forwarded to the chip's port 2412");
        using var gpio = new GpioServer(sim, a => { board.Read(_ => { a(); return 0; }); board.Rerun(); }, lan: args.Contains("--lan"))
        {
            FirmwarePart = mcu,
        };
        Console.Error.WriteLine($"[boardsim] GPIO: {string.Join("  ", gpio.Addresses())}   e.g.  nc localhost {gpio.Port}");
        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        var last = 0;
        while (!stop.Wait(1000))
        {
            var (frames, strings) = board.Read(b => (b.Frames, b.Strings.Count));
            if (frames != last) { Console.Error.WriteLine($"[boardsim] {frames} LED frames on {strings} strings"); last = frames; }
        }
        return 0;
    }
    case "gpio":
    {
        var report = Checker.Run(path);
        var sim = new Simulator(report.Circuit);
        sim.Run();
        var gate = new object();
        using var gpio = new GpioServer(sim, a => { lock (gate) { a(); sim.Run(); } }, lan: args.Contains("--lan"));
        gpio.Activity += what => Console.Error.WriteLine($"[boardsim] {what}");
        Console.Error.WriteLine($"[boardsim] {report.Board.Title}: pins on {string.Join("  ", gpio.Addresses())}   (Ctrl+C to stop)");
        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();
        return 0;
    }
    default:
        Console.Error.WriteLine($"Unknown command {command}");
        return 2;
}
