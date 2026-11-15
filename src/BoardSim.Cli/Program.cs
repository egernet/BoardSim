using System.Globalization;
using BoardSim.Core.Analysis;
using BoardSim.Core.Components;
using BoardSim.Core.Emc;
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
          boardsim 3d    <board.kicad_pcb>           build the 3D model with the real part models (needs KiCad)
          boardsim emc   <board.kicad_pcb> [--parts] noise and emission estimates: signals, return paths,
                                                     crosstalk and radiated emission against CISPR 32 class B

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
    case "3d":
    {
        var report = Checker.Run(path);
        var components = ComponentData.For(report.Circuit);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = Board3D.BuildAsync(report.Circuit, components, Console.WriteLine).GetAwaiter().GetResult();
        Console.WriteLine($"{mesh.Source}: {mesh.Triangles:N0} triangles, {mesh.Positions.Length / 3:N0} vertices in {sw.Elapsed.TotalSeconds:0.0} s");
        Console.WriteLine($"bounds {mesh.Min} - {mesh.Max} mm");
        foreach (var side in new[] { BoardSim.Core.Model.Side.Front, BoardSim.Core.Model.Side.Back })
        {
            sw.Restart();
            if (BoardRenders.RenderAsync(report.Board, components, side).GetAwaiter().GetResult() is { } r)
                Console.WriteLine($"{side}: {r.WithParts.Image} covers {r.WithParts.Area} ({sw.Elapsed.TotalSeconds:0.0} s)");
        }
        return 0;
    }
    case "emc":
    {
        var report = Checker.Run(path);
        var emc = EmcAnalysis.For(report.Circuit, report.Expectations);
        var st = emc.Routes.Stackup;
        string F(double v, string fmt = "0.#") => v.ToString(fmt, CultureInfo.InvariantCulture);
        Console.WriteLine($"{report.Board.Title}: stackup {(st.FromFile ? "from the board file" : "assumed (JLC standard)")}: " +
                          string.Join(" | ", st.Copper.Select(l => $"{l} {F(st.Z(l), "0.###")} mm")));
        foreach (var (layer, net, area) in emc.Routes.Planes.Planes().OrderBy(p => st.Z(p.Layer)))
            Console.WriteLine($"  plane {layer,-7} {net,-12} {F(area, "0")} mm²");
        Console.WriteLine();
        Console.WriteLine($"{"net",-16} {"signal",-26} {"edge",7} {"I",7} {"len",6} {"vias",4} {"loop",8} {"no ref",6}  references");
        foreach (var a in emc.Active)
        {
            var r = a.Route;
            var refs = string.Join(", ", r.ByReference.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {F(x.Value, "0")}"));
            Console.WriteLine($"{Trim(a.Net.DisplayName, 16),-16} {Trim($"{a.Signal.Name} {Hz(a.Signal.Hz)}", 26),-26} {F(a.EdgeNs, "0.0") + "ns",7} {F(a.CurrentA * 1000, "0") + "mA",7} " +
                              $"{F(r.Length, "0"),6} {r.Vias,4} {F(r.LoopArea, "0.0") + "mm²",8} {F(r.Unreferenced, "0"),6}  {refs}");
        }
        Console.WriteLine();
        Console.WriteLine("Radiated emission estimate at 3 m (CISPR 32 class B: 40 dBµV/m below 230 MHz, 47 above):");
        foreach (var (a, peak) in emc.Active.Select(a => (a, emc.Peak(a))).OrderBy(x => x.Item2.Margin).Take(12))
            Console.WriteLine($"  {Trim(a.Net.DisplayName, 16),-16} peak {F(peak.Level, "0")} dBµV/m at {Hz(peak.Hz),-9} margin {F(peak.Margin, "0"),4} dB" +
                              (a.LeavesBoard ? $"   (cable on {string.Join(",", a.Exits.Select(e => e.Part.Reference).Distinct())}, {F(a.CableMetres)} m)" : ""));
        Console.WriteLine();
        Console.WriteLine("Crosstalk (backward, estimate):");
        foreach (var x in emc.Crosstalk().Take(8))
            Console.WriteLine($"  {Trim(x.Aggressor.Net.DisplayName, 14),-14} -> {Trim(x.Victim.DisplayName, 14),-14} {F(x.Percent, "0.00")} %  ({F(x.ParallelMm, "0")} mm parallel, gap {F(x.GapMm, "0.00")} mm, {x.Layer})");
        Console.WriteLine();
        Console.WriteLine("Edge data: " + string.Join("; ", emc.Active.Select(a => a.Signal.Data).Distinct()));
        if (args.Contains("--parts"))
        {
            Console.WriteLine();
            Console.WriteLine($"{"part",-7} {"value",-24} {"part model",-22} {"3D model",-12} {"height",7}  file");
            foreach (var c in ComponentData.For(report.Circuit).OrderBy(c => c.Part.Footprint.Prefix).ThenBy(c => c.Part.Reference.Length).ThenBy(c => c.Part.Reference))
                Console.WriteLine($"{c.Part.Reference,-7} {Trim(c.Part.Footprint.Value, 24),-24} {c.PartModel ?? "generic",-22} " +
                                  $"{c.Model3D?.Source.ToString() ?? "none",-12} {(c.Model3D?.HeightMm is { } hm ? F(hm, "0.0") + " mm" : ""),7}  {c.Model3D?.File ?? c.Model3D?.Model.Path ?? ""}");
        }
        return 0;

        static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
        static string Hz(double hz) => hz >= 1e6 ? (hz / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + " MHz" : (hz / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";
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
