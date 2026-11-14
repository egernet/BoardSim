using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace BoardSim.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            desktop.MainWindow = new MainWindow(StartupOptions.Parse(args));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Command line: [board.kicad_pcb] [--screenshot out.png] [--select N] [--side back] [--layers]
/// [--net NAME] [--simulate] [--drive U1.GPIO7=H ...] [--firmware build-folder] [--power] [--wait ms] [--probe U1.GPIO7 ...] [--scope led] [--crystal Y1=26]
/// </summary>
public sealed record StartupOptions(string? BoardPath, string? Screenshot, int? SelectFinding, bool Back, bool Layers, string? Net,
    bool Simulate, IReadOnlyList<(string Pin, BoardSim.Core.Simulation.PinDrive Drive)> Drives, string? Firmware, int Wait, bool Power, IReadOnlyList<string> Probes, bool ScopeLed, string? Crystal)
{
    public static StartupOptions Parse(string[] args)
    {
        string? board = null, shot = null, net = null, firmware = null;
        var wait = 700;
        int? select = null;
        bool back = false, layers = false, simulate = false, power = false;
        var drives = new List<(string, BoardSim.Core.Simulation.PinDrive)>();
        var probes = new List<string>();
        var scopeLed = false;
        string? crystal = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--screenshot" when i + 1 < args.Length: shot = args[++i]; break;
                case "--select" when i + 1 < args.Length: select = int.Parse(args[++i]); break;
                case "--net" when i + 1 < args.Length: net = args[++i]; break;
                case "--side" when i + 1 < args.Length: back = args[++i] == "back"; break;
                case "--layers": layers = true; break;
                case "--simulate": simulate = true; break;
                case "--firmware" when i + 1 < args.Length: firmware = args[++i]; simulate = true; break;
                case "--wait" when i + 1 < args.Length: wait = int.Parse(args[++i]); break;
                case "--power": power = true; simulate = true; break;
                case "--probe" when i + 1 < args.Length: probes.Add(args[++i]); break;
                case "--scope" when i + 1 < args.Length: scopeLed = args[++i] == "led"; break;
                case "--crystal" when i + 1 < args.Length: crystal = args[++i]; break;
                case "--drive" when i + 1 < args.Length:
                    var kv = args[++i].Split('=');
                    drives.Add((kv[0], kv[1].ToUpperInvariant() switch
                    {
                        "H" or "1" => BoardSim.Core.Simulation.PinDrive.High,
                        "L" or "0" => BoardSim.Core.Simulation.PinDrive.Low,
                        _ => BoardSim.Core.Simulation.PinDrive.Input,
                    }));
                    simulate = true;
                    break;
                default: if (!args[i].StartsWith("--")) board = args[i]; break;
            }
        }
        return new StartupOptions(board, shot, select, back, layers, net, simulate, drives, firmware, wait, power, probes, scopeLed, crystal);
    }
}
