using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BoardSim.App.Controls;
using BoardSim.App.Rendering;
using BoardSim.Core.Analysis;
using BoardSim.Core.Components;
using Board3DMesh = BoardSim.Core.Components.Board3D;
using BoardSim.Core.Emc;
using BoardSim.Core.Model;
using BoardSim.Core.Simulation;
using BoardSim.Core.Emulation;

namespace BoardSim.App;

public partial class MainWindow : Window
{
    private readonly StartupOptions _options;
    private BoardReport? _report;
    private List<FindingItem> _findings = new();
    private Simulator? _sim;
    private EmulatedChip? _chip;
    private GpioServer? _gpio;
    private EmulatedBoard? _emu;
    private string? _chipStatus;
    private int _emuVersion = -1;
    private readonly System.Text.StringBuilder _console = new();
    private readonly DispatcherTimer _emuTimer;
    private readonly Dictionary<PinRef, (TextBlock Volts, ToggleButton In, ToggleButton High, ToggleButton Low)> _pinRows = new();
    private readonly List<Probe> _probes = new();
    private readonly Dictionary<Net, List<(double T, double V)>> _history = new();
    private readonly DispatcherTimer _scopeTimer;
    private Dictionary<int, ScopeTrace?>? _frozen;
    private enum TrigMode { Auto, Normal, Single }
    private TrigMode _trigMode = TrigMode.Auto;
    private bool _armed;
    private long _armNs;
    private double _armWall;

    public MainWindow() : this(StartupOptions.Parse(Array.Empty<string>())) { }

    public MainWindow(StartupOptions options)
    {
        _options = options;
        InitializeComponent();

        OpenButton.Click += async (_, _) => await PickBoard();
        ExampleButton.Click += (_, _) => OpenExample();
        FitButton.Click += (_, _) => Board.Fit();
        TopButton.Click += (_, _) => SetSide(Side.Front);
        BottomButton.Click += (_, _) => SetSide(Side.Back);
        RealButton.Click += (_, _) => SetMode(ViewMode.Realistic);
        LayersButton.Click += (_, _) => SetMode(ViewMode.Layers);
        View3DButton.Click += (_, _) => _ = Set3D(View3DButton.IsChecked == true);
        SimButton.Click += (_, _) => SetSimulating(SimButton.IsChecked == true);
        FirmwareButton.Click += (_, _) =>
        {
            if (_chip?.IsRunning == true) PowerOff();
            else PowerOn();
        };
        FlashButton.Click += async (_, _) => await PickFirmware();
        CopyPort.Click += async (_, _) =>
        {
            if (_chip is not null && Clipboard is not null) await Clipboard.SetTextAsync(_chip.Port.Url);
        };
        ResetChip.Click += (_, _) => _chip?.Reset();
        GpioLan.Click += (_, _) => { _gpio?.SetLan(GpioLan.IsChecked == true); UpdateGpioCard(); };
        ConsoleInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _chip is null) return;
            _chip.WriteConsole(ConsoleInput.Text + "\n");
            ConsoleInput.Text = "";
            e.Handled = true;
        };
        _emuTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => RefreshEmulation());
        Closing += (_, _) => { PowerOff(); _chip?.Dispose(); _gpio?.Dispose(); };
        Board.SwitchHeld += (fp, held) =>
        {
            if (_sim?.Circuit.Part(fp.Reference) is not { } sw) return;
            Press(sw, held);
        };
        ProbeButton.Click += (_, _) =>
        {
            if (ProbeButton.IsChecked == true)
            {
                if (SimButton.IsChecked != true) SetSimulating(true);
                ScopePanel.IsVisible = true;
                Status.Text = "Probe: click pads on the board to clip probes on (up to 4). Click a probed net again to remove it.";
            }
            UpdateScope();
        };
        ScopeRoll.Click += (_, _) => SetScopeMode(ScopeMode.Roll);
        ScopeRun.Click += (_, _) => SetScopeMode(ScopeMode.Run);
        ScopeAuto.Click += (_, _) => AutoScope(null);
        Scope.AutoRequested += probe => AutoScope(probe);
        Scope.Trace = probe => _frozen is not null ? _frozen.GetValueOrDefault(probe.Channel) : TraceFor(probe.Net);
        ScopeHold.Click += (_, _) =>
        {
            if (ScopeHold.IsChecked == true)
                _frozen = _probes.ToDictionary(p => p.Channel, p => TraceFor(p.Net));
            else if (_trigMode == TrigMode.Single) Arm();     // releasing a single capture re-arms it
            else { _frozen = null; Scope.TriggerAtNs = null; Scope.RollNow = null; }
            Scope.InvalidateVisual();
        };
        TrigAuto.Click += (_, _) => SetTrigMode(TrigMode.Auto);
        TrigNormal.Click += (_, _) => SetTrigMode(TrigMode.Normal);
        TrigSingle.Click += (_, _) => SetTrigMode(TrigMode.Single);
        ScopeEdge.Click += (_, _) =>
        {
            Scope.Edge = Scope.Edge switch { TriggerEdge.Rising => TriggerEdge.Falling, TriggerEdge.Falling => TriggerEdge.Either, _ => TriggerEdge.Rising };
            ScopeEdge.Content = Scope.Edge switch { TriggerEdge.Rising => "↑ Rising", TriggerEdge.Falling => "↓ Falling", _ => "↕ Either" };
            if (_armed) Arm();
            Scope.InvalidateVisual();
        };
        Scope.TriggerSourceRequested += probe =>
        {
            Scope.TriggerChannel = probe.Channel;
            Status.Text = $"Trigger on CH{probe.Channel} {probe.Net.DisplayName}";
            if (_trigMode != TrigMode.Auto) Arm();
            Scope.InvalidateVisual();
        };
        ScopeSlower.Click += (_, _) => { Scope.Zoom(+1); UpdateScope(); };
        ScopeFaster.Click += (_, _) => { Scope.Zoom(-1); UpdateScope(); };
        ScopeClear.Click += (_, _) => { _probes.Clear(); _history.Clear(); UpdateScope(); };
        Scope.History = net => _history.TryGetValue(net, out var h) ? h : Array.Empty<(double, double)>();
        Scope.Now = net => _sim?.Nets.TryGetValue(net, out var r) == true ? r.Volts : null;
        _scopeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            if (!ScopePanel.IsVisible) return;
            SampleProbes();
            if (_armed) TriggerStep();
            if (ScopeHold.IsChecked != true) Scope.InvalidateVisual();
        });
        _scopeTimer.Start();
        PartsButton.Click += (_, _) => { Board.ShowParts = PartsButton.IsChecked == true; Board.InvalidateVisual(); };

        FindingsList.SelectionChanged += (_, _) => SelectFinding(FindingsList.SelectedItem as FindingItem);
        EmcList.SelectionChanged += (_, _) =>
        {
            if (EmcList.SelectedItem is not EmcNetItem item) return;
            Spectrum.Selected = item.Net;
            Spectrum.InvalidateVisual();
            Board.Highlight(new[] { item.Net.Net.Name }, Array.Empty<string>(), item.Net.Route.Tracks.FirstOrDefault()?.Start);
        };
        PartsList.SelectionChanged += (_, _) =>
        {
            if (PartsList.SelectedItem is PartItem p && _report?.Board.Find(p.Reference) is { } fp)
            {
                Board.Highlight(Array.Empty<string>(), new[] { fp.Reference });
                Board.FocusOn(fp.Position, 22);
            }
        };

        Board.PadClicked += OnPadClicked;
        Board.BodyClicked += fp =>
        {
            if (_sim?.Circuit.Part(fp.Reference) is { Kind: PartKind.Crystal } xtal) { ShowCrystal(xtal); return; }
            Board.Highlight(Array.Empty<string>(), new[] { fp.Reference });
        };
        Board.CursorMoved += p => CursorText.Text = $"x {p.X,7:0.00}  y {p.Y,7:0.00} mm";
        Board.PadHovered += pad => Status.Text = pad is null ? StatusLine() : Describe(pad);

        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Board.ClearHighlight(); FindingsList.SelectedItem = null; }
            if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Meta | KeyModifiers.Control)) _ = PickBoard();
        };

        Opened += async (_, _) =>
        {
            if (_options.BoardPath is { } path) LoadBoard(path);
            if (_options.Back) SetSide(Side.Back);
            if (_options.Layers) SetMode(ViewMode.Layers);
            if (_options.Simulate) SetSimulating(true);
            foreach (var spec in _options.Probes)
                if (_sim is not null && ExpectationRule.Resolve(_sim.Circuit, spec) is { } probePin) ToggleProbe(probePin.Net, probePin.Pad);
            if (_options.ScopeLed) { SetScopeMode(ScopeMode.Run); _ = AutoSoon(); }
            if (_options.View3D) _ = Set3D(true);
            if (_options.Tab is { } tabName)
                Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => string.Equals(t.Header as string, tabName, StringComparison.OrdinalIgnoreCase));
            if (_options.EmcNet is { } emcNet)
                _ = Task.Delay(2500).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                    EmcList.SelectedItem = EmcList.ItemsSource?.OfType<EmcNetItem>().FirstOrDefault(i => i.Net.Net.DisplayName == emcNet)));
            if (_options.PerDiv is { } perDiv)
                _ = Task.Delay(Math.Max(1000, _options.Wait - 2500)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { Scope.SetPerDiv(perDiv); UpdateScope(); }));
            if (_options.Trigger is { } trig)
            {
                // e.g. "single:3:falling" - mode, channel, edge
                var parts = trig.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[1], out var tch)) Scope.TriggerChannel = tch;
                if (parts.Length > 2) Scope.Edge = Enum.Parse<TriggerEdge>(parts[2], true);
                ScopeEdge.Content = Scope.Edge switch { TriggerEdge.Rising => "↑ Rising", TriggerEdge.Falling => "↓ Falling", _ => "↕ Either" };
                _ = Task.Delay(9000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                    SetTrigMode(parts[0] == "normal" ? TrigMode.Normal : TrigMode.Single)));
            }
            if (_options.Firmware is { } fw) FlashBuild(fw);
            else if (_options.Power) PowerOn();
            if (_options.Crystal is { } xs && xs.Split('=') is [var xref, var xmhz] && _sim?.Circuit.Part(xref) is { } xpart)
            {
                _sim.SetCrystalHz(xpart, double.Parse(xmhz, System.Globalization.CultureInfo.InvariantCulture) * 1e6);
                ShowCrystal(xpart);
            }
            foreach (var (spec, drive) in _options.Drives)
                if (_sim is not null && ExpectationRule.Resolve(_sim.Circuit, spec) is { } dp) { _sim.SetPin(dp, drive); RunSimulation(); }
            if (_options.Net is { } net) { Board.Highlight(new[] { net }, Array.Empty<string>()); }
            if (_options.SelectFinding is { } n && n < _findings.Count) FindingsList.SelectedIndex = n;
            if (_options.Screenshot is { } shot)
            {
                await Task.Delay(_options.Wait);
                await SaveScreenshot(shot);
                Close();
            }
        };
    }

    private void SetSide(Side side)
    {
        TopButton.IsChecked = side == Side.Front;
        BottomButton.IsChecked = side == Side.Back;
        Board.SetSide(side);
    }

    private void SetMode(ViewMode mode)
    {
        _ = Set3D(false);
        RealButton.IsChecked = mode == ViewMode.Realistic;
        LayersButton.IsChecked = mode == ViewMode.Layers;
        Board.SetMode(mode);
    }

    private IReadOnlyList<ComponentInfo>? _components;
    private BoardReport? _meshFor;

    /// <summary>Shows the board in 3D with the real models; the first time per board version KiCad builds the mesh.</summary>
    private async Task Set3D(bool on)
    {
        View3DButton.IsChecked = on;
        Board3D.IsVisible = on;
        Board.IsVisible = !on;
        Board3DStatusBox.IsVisible = on;
        if (!on) return;
        RealButton.IsChecked = LayersButton.IsChecked = false;
        if (_report is not { } r || _meshFor == r) return;
        _meshFor = r;
        Board3DStatus.Text = "Finding the 3D models…";
        var components = _components ?? await Task.Run(() => ComponentData.For(r.Circuit));
        var models = components.Count(c => c.Model3D is { Source: not DataSource.Missing });
        Board3DStatus.Text = Board3DMesh.FindKiCadCli() is null ? "KiCad not found: drawing boxes" : $"Building the 3D model with KiCad ({models} part models)… the first time takes up to a minute";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var mesh = await Task.Run(() => Board3DMesh.BuildAsync(r.Circuit, components));
        if (_report != r) return;
        Board3D.Mesh = mesh;
        var missing = components.Count(c => c.Model3D is { Source: DataSource.Missing });
        Board3DStatus.Text = $"{mesh.Source} · {mesh.Triangles:N0} triangles · {models} models" +
                             (missing > 0 ? $", {missing} missing (see Parts)" : "") +
                             (Board3D.Error is { } err ? $" · OpenGL: {err}" : "") +
                             " · drag to turn, scroll to zoom, shift-drag to move";
    }

    private async Task PickBoard()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a KiCad board",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("KiCad board") { Patterns = new[] { "*.kicad_pcb" } } },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) LoadBoard(path);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p?.EndsWith(".kicad_pcb") == true);
        if (file is not null) LoadBoard(file);
    }

    private void LoadBoard(string path)
    {
        try
        {
            Status.Text = $"Loading {Path.GetFileName(path)}…";
            _report = Checker.Run(path);
            _components = null;
        }
        catch (Exception ex)
        {
            Status.Text = $"Could not load {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        var r = _report;
        Board.SetScene(new BoardScene(r.Board, r.Circuit));
        EmptyState.IsVisible = false;
        BoardName.Text = Path.GetFileName(path);
        Title = $"BoardSim – {r.Board.Title}";

        ErrorCount.Text = r.Errors.ToString();
        WarningCount.Text = r.Warnings.ToString();
        InfoCount.Text = r.Findings.Count(f => f.Severity == Severity.Info).ToString();
        var modelled = r.Circuit.Parts.Count(p => p.Model is not null);
        var b = r.Board.Bounds();
        BoardStats.Text = $"{r.Board.Footprints.Count} parts · {r.Circuit.Nets.Count} nets · {r.Board.CopperLayers.Count} layers · {b.Width:0.#} × {b.Height:0.#} mm\n" +
                          $"{modelled} parts with a model" +
                          (r.Expectations is null ? "" : $" · {r.Expectations.Connections.Count} expected connections checked");

        _findings = r.Findings.Select(f => new FindingItem(f)).ToList();
        FindingsList.ItemsSource = _findings;
        AllClear.IsVisible = _findings.Count == 0;
        AllClearDetail.Text = $"All nets are routed and {modelled} modelled parts pass their pin checks" +
                              (r.Expectations is null ? "." : $", and all {r.Expectations.Connections.Count} expected connections exist.");
        PartsList.ItemsSource = r.Circuit.Parts
            .OrderBy(p => p.Footprint.Prefix).ThenBy(p => p.Reference.Length).ThenBy(p => p.Reference)
            .Select(p => new PartItem(p, null)).ToList();
        _ = LoadComponentData(r);
        if (View3DButton.IsChecked == true) _ = Set3D(true);
        Status.Text = StatusLine();
        Tabs.SelectedIndex = 0;

        _sim = new Simulator(r.Circuit);
        _sim.Run();
        StartGpioServer();
        if (SimButton.IsChecked == true) { Board.Simulation = _sim; BuildSimPanel(); }
    }

    /// <summary>3D models and the EMC estimate take a moment: work them out in the background, then fill the Parts and EMC tabs.</summary>
    private async Task LoadComponentData(BoardReport r)
    {
        EmcSummary.Text = "Estimating noise and emission…";
        var (components, emc) = await Task.Run(() => (ComponentData.For(r.Circuit), EmcAnalysis.For(r.Circuit, r.Expectations)));
        if (_report != r) return;
        _components = components;
        _ = LoadBoardRenders(r, components);
        var byPart = components.ToDictionary(c => c.Part);
        PartsList.ItemsSource = r.Circuit.Parts
            .OrderBy(p => p.Footprint.Prefix).ThenBy(p => p.Reference.Length).ThenBy(p => p.Reference)
            .Select(p => new PartItem(p, byPart.GetValueOrDefault(p))).ToList();

        Spectrum.Analysis = emc;
        Spectrum.Selected = null;
        Spectrum.InvalidateVisual();
        var st = emc.Routes.Stackup;
        var found = components.Count(c => c.Model3D is { Source: not DataSource.Missing });
        var missing = components.Count(c => c.Model3D is { Source: DataSource.Missing });
        var generic = components.Count(c => c.IsGeneric && c.Part.Kind is PartKind.Ic or PartKind.Regulator);
        EmcSummary.Text =
            $"{emc.Active.Count} switching nets. Stackup {(st.FromFile ? "from the board" : "assumed")}: " +
            string.Join(" · ", st.Copper.Select(l => $"{l} {st.Z(l):0.##} mm")) + ". " +
            $"Edge data from the part models; {(generic == 0 ? "every IC has one" : $"{generic} IC{(generic == 1 ? "" : "s")} use generic values")}. " +
            $"3D models: {found} found{(missing > 0 ? $", {missing} missing" : "")}. " +
            "Estimates, typically within 10-20 dB of a chamber measurement: use them to compare layouts and find the worst nets.";
        EmcList.ItemsSource = emc.Active.Select(a => new EmcNetItem(emc, a)).OrderBy(i => i.Margin).ToList();
    }

    /// <summary>KiCad's own top and bottom renders (real part models), shown in the realistic view once they are ready.</summary>
    private async Task LoadBoardRenders(BoardReport r, IReadOnlyList<ComponentInfo> components)
    {
        Board.ClearRenders();
        if (Board3DMesh.FindKiCadCli() is null) return;
        foreach (var side in new[] { Side.Front, Side.Back })
        {
            try
            {
                var renders = await Task.Run(() => BoardRenders.RenderAsync(r.Board, components, side));
                if (_report != r || renders is not { } rr) return;
                foreach (var x in new[] { rr.WithParts, rr.Bare })
                    Board.SetRender(side, x.WithParts, new BoardView.KiCadRender(new Avalonia.Media.Imaging.Bitmap(x.Image), x.Area, x.Mirrored));
            }
            catch (Exception e)
            {
                Status.Text = $"KiCad render failed: {e.Message}";
                return;
            }
        }
    }

    private void StartGpioServer()
    {
        var lan = _gpio?.Lan ?? false;
        _gpio?.Dispose();
        _gpio = null;
        if (_sim is null) return;
        var sim = _sim;
        // Clients run on server threads; changes go through the UI thread (and the
        // emulation lock when firmware runs), exactly like a click in the app.
        _gpio = new GpioServer(sim, action => Dispatcher.UIThread.Invoke(() =>
        {
            if (_sim != sim) return;
            if (_emu is not null) { _emu.Read(_ => { action(); return 0; }); _emu.Rerun(); }
            else { action(); RunSimulation(); }
        }), lan: lan);
        _gpio.Activity += what => Dispatcher.UIThread.Post(() => { if (_emu is null) Status.Text = $"GPIO client: {what}"; });
        UpdateGpioCard();
    }

    private void UpdateGpioCard()
    {
        if (_gpio is null) { GpioUrl.Text = "Open a board"; GpioHint.Text = ""; return; }
        GpioLan.IsChecked = _gpio.Lan;
        GpioUrl.Text = string.Join("   ", _gpio.Addresses());
        var host = _gpio.Addresses().Last();
        GpioHint.Text = $"Drive and watch pins from outside, e.g. a Raspberry Pi:  nc {host.Replace(":", " ")}  then  watch J1 · set GPIO17 high · help. " +
                        "Python: clients/python/boardsim_gpio.py (RPi.GPIO style) and pi_bridge.py (real Pi pins).";
    }

    /// <summary>examples/julelys-v4 next to the app (packaged) or in the repository (development).</summary>
    private static string? FindExample()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var board = Path.Combine(d.FullName, "examples", "julelys-v4", "julelys_v4.kicad_pcb");
            if (File.Exists(board)) return board;
        }
        return null;
    }

    private void OpenExample()
    {
        if (FindExample() is not { } board) { Status.Text = "The example was not found next to the app."; return; }
        LoadBoard(board);
        var firmware = Path.Combine(Path.GetDirectoryName(board)!, "firmware");
        if (Directory.Exists(firmware) && Firmware.FindQemu() is not null) FlashBuild(firmware);
        else SetSimulating(true);
    }

    private async Task PickFirmware()
    {
        if (_report is null) { Status.Text = "Open a board first."; return; }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Pick the ESP-IDF build folder of the firmware (the folder with flash_args)",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) FlashBuild(path);
    }

    /// <summary>The board's chip, created on first use; null (with a status line) when it cannot be emulated.</summary>
    private EmulatedChip? Chip()
    {
        if (_chip is not null) return _chip;
        if (_report is null || _sim is null) { Status.Text = "Open a board first."; return null; }
        var mcu = EmulatedBoard.FindMcu(_report.Circuit);
        if (mcu is null) { Status.Text = "This board has no ESP32-C3 BoardSim can emulate."; return null; }
        if (Firmware.FindQemu() is not { } qemu)
        {
            Status.Text = "BoardSim's QEMU was not found. Set BOARDSIM_QEMU to qemu-system-riscv32 from github.com/egernet/qemu-boardsim.";
            return null;
        }
        _emu = new EmulatedBoard(_sim, mcu, _report.Expectations?.LedOrder ?? "GRB");
        _chip = new EmulatedChip(_emu, qemu, EmulatedChip.DefaultFlashPath(_report.Board.FilePath));
        _chip.Console += text => { lock (_console) _console.Append(text); };
        _chip.Status += line => _chipStatus = line;
        _chip.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            if (_chip is null) return;
            Status.Text = $"QEMU stopped (exit code {code}). {_chip.Log.Split('\n').LastOrDefault(l => l.Trim().Length > 0)}";
            PowerOff();
        });
        PortUrl.Text = _chip.Port.Url;
        PortHint.Text = $"Flash and watch it like a real board:  idf.py -p {_chip.Port.Url} flash monitor";
        return _chip;
    }

    private void PowerOn()
    {
        if (Chip() is not { } chip) return;
        lock (_console) _console.Clear();
        ConsoleText.Text = "";
        try { chip.PowerOn(); }
        catch (Exception ex) { Status.Text = ex.Message; return; }
        OnPowered(chip);
        Status.Text = chip.FlashIsBlank()
            ? $"{chip.Board.Mcu.Reference} powered with blank flash. Flash it: idf.py -p {chip.Port.Url} flash"
            : $"{chip.Board.Mcu.Reference} powered, booting from its flash";
    }

    private void FlashBuild(string path)
    {
        if (Chip() is not { } chip) return;
        lock (_console) _console.Clear();
        ConsoleText.Text = "";
        try { chip.LoadBuild(path); }
        catch (Exception ex) { Status.Text = ex.Message; return; }
        OnPowered(chip);
        Status.Text = $"Flashed {Path.GetFileName(path.TrimEnd('/', '\\'))} and booted it";
    }

    private void OnPowered(EmulatedChip chip)
    {
        if (_gpio is not null) _gpio.FirmwarePart = chip.Board.Mcu;
        _emuTimer.Start();
        SetSimulating(true);
        FirmwareLabel.Text = "Power off";
        FirmwareIcon.Stroke = new SolidColorBrush(Color.Parse("#41d98a"));
        BuildSimPanel();
    }

    private void PowerOff()
    {
        if (_gpio is not null) _gpio.FirmwarePart = null;
        _emuTimer?.Stop();
        _chip?.PowerOff();
        Strings?.Update(Array.Empty<LedString>());
        if (FirmwareLabel is not null)
        {
            FirmwareLabel.Text = "Power on";
            FirmwareIcon.Stroke = new SolidColorBrush(Color.Parse("#3bd1ff"));
        }
        // The chip is off: no more messages arrive, so the UI may drive the simulation again.
        if (_sim is not null && _emu is not null)
            _emu.Read(_ => { foreach (var p in _sim.ControllablePins) _sim.SetPin(p, PinDrive.Input); return 0; });
        _emu = null;
        if (_sim is not null) RunSimulation();
        _chip?.Dispose();
        _chip = null;
        PortUrl.Text = "Power on the chip to get its USB port";
        PortHint.Text = "";
    }

    private void RefreshEmulation()
    {
        if (_emu is null || _chip is null) return;
        string? text = null;
        lock (_console)
        {
            if (_console.Length > 0) { text = _console.ToString(); _console.Clear(); }
        }
        if (text is not null)
        {
            var all = (ConsoleText.Text ?? "") + text.Replace("\r", "");
            if (all.Length > 60000) all = all[^50000..];
            ConsoleText.Text = all;
            ConsoleText.CaretIndex = all.Length;
        }

        var (version, strings, frames) = _emu.Read(e => (e.Version, e.Strings.Values.Select(s => new LedString
        {
            Pin = s.Pin, Leds = s.Leds, Frames = s.Frames, LastFrame = s.LastFrame,
        }).ToList(), e.Frames));
        if (version == _emuVersion && _chipStatus is null) return;
        _emuVersion = version;
        Strings.Update(strings);
        RunSimulation(solve: false);
        Board.InvalidateVisual();
        var lit = _sim?.LedCurrent.Count(l => l.Value > 0) ?? 0;
        var mode = _chip.DownloadMode ? "download mode (esptool)" : _chip.HeldInReset ? "held in reset" : "running";
        var stream = _chip.ActiveStreamPort is { } sp ? $" · stream: udp 127.0.0.1:{sp}" : "";
        Status.Text = $"Chip {mode} · {frames} LED frames · {strings.Count} strings · {lit} board LEDs lit{stream}" +
                      (_chipStatus is { } st ? $" · {st}" : "");
        _chipStatus = null;
    }

    private void SetSimulating(bool on)
    {
        SimButton.IsChecked = on;
        Board.Simulation = on ? _sim : null;
        if (on) { BuildSimPanel(); Tabs.SelectedItem = SimTab; RunSimulation(); }
        Board.InvalidateVisual();
    }

    private void Press(Part sw, bool held)
    {
        if (_sim is null) return;
        if (_emu is not null)
        {
            _emu.Read(_ => { _sim.SetPressed(sw, held); return 0; });
            _emu.Rerun();
            return;
        }
        _sim.SetPressed(sw, held);
        RunSimulation();
    }

    private void RunSimulation(bool solve = true)
    {
        if (_sim is null) return;
        if (_emu is not null) solve = false;   // the firmware owns the pins; EmulatedBoard solves
        if (solve) _sim.Run();
        SampleProbes();
        foreach (var (pin, row) in _pinRows)
        {
            var drive = _sim.DriveOf(pin);
            row.In.IsChecked = drive == PinDrive.Input;
            row.High.IsChecked = drive == PinDrive.High;
            row.Low.IsChecked = drive == PinDrive.Low;
            row.Volts.Text = VoltsText(pin.Net);
            row.Volts.Foreground = new SolidColorBrush(_sim.Nets.TryGetValue(pin.Net, out var res) ? BoardView.VoltageColor(res) : Colors.Gray);
        }
        if (_emu is not null) return;
        var conflicts = _sim.Nets.Where(n => n.Value.State == NetState.Conflict).ToList();
        var lit = _sim.LedCurrent.Count(l => l.Value > 0);
        Status.Text = conflicts.Count > 0
            ? $"Conflict on {string.Join(", ", conflicts.Select(c => c.Key.DisplayName))}: {conflicts[0].Value.Note}"
            : $"Simulated in {_sim.Iterations} passes · {lit} LEDs lit · hold a button on the board to press it";
        Board.InvalidateVisual();
    }

    private string VoltsText(Net net) =>
        _sim?.Nets.TryGetValue(net, out var r) != true ? "" :
        r!.State == NetState.Floating ? "floating" :
        r.State == NetState.Conflict ? $"{r.Volts:0.00} V conflict" : $"{r.Volts:0.00} V";

    private void BuildSimPanel()
    {
        if (_sim is null) return;
        SimPanel.Children.Clear();
        _pinRows.Clear();
        SimPanel.Children.Add(Note("The board is powered. Drive a pin with H or L (or click its pad on the board), hold SW buttons with the mouse, and follow the colours."));

        var pins = _sim.ControllablePins.ToList();
        foreach (var part in pins.GroupBy(p => p.Part).OrderBy(g => g.Key.Kind == PartKind.Connector).ThenBy(g => g.Key.Reference))
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var pin in part.OrderBy(p => SortKey(p.Name)))
                rows.Children.Add(PinRow(pin));
            var header = $"{part.Key.Reference} · {part.Key.Footprint.Value}";
            if (part.Key.Kind == PartKind.Connector)
                SimPanel.Children.Add(new Expander
                {
                    Header = new TextBlock { Text = header, FontWeight = FontWeight.SemiBold },
                    Content = rows, HorizontalAlignment = HorizontalAlignment.Stretch,
                });
            else
                SimPanel.Children.Add(Card(Heading(header),
                    Note(_emu is not null && part.Key == _emu.Mcu
                        ? "Driven by the firmware running in QEMU. Inputs (buttons, pulls) are sent back to it."
                        : "No firmware yet: pins are inputs until you drive them. Use Run firmware… to run real code."),
                    rows));
        }

        var switches = _sim.Switches.ToList();
        if (switches.Count > 0)
        {
            var row = new WrapPanel();
            foreach (var sw in switches)
            {
                var b = new Button { Classes = { "tool" }, Content = $"Hold {sw.Reference}", Margin = new Thickness(0, 0, 6, 6) };
                b.AddHandler(PointerPressedEvent, (_, _) => Press(sw, true), RoutingStrategies.Tunnel);
                b.AddHandler(PointerReleasedEvent, (_, _) => Press(sw, false), RoutingStrategies.Tunnel);
                row.Children.Add(b);
            }
            SimPanel.Children.Add(Card(Heading("Buttons"), row));
        }
        RunSimulation();
    }

    private static string SortKey(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return new string(name.Where(c => !char.IsDigit(c)).ToArray()) + (digits.Length > 0 ? digits.PadLeft(4, '0') : "");
    }

    private Control PinRow(PinRef pin)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("88,*,Auto"), Margin = new Thickness(0, 1) };
        var name = new StackPanel { Spacing = 0 };
        name.Children.Add(new TextBlock { Text = pin.Name, FontSize = 12, FontWeight = FontWeight.SemiBold });
        name.Children.Add(new TextBlock { Text = pin.Net.DisplayName, FontSize = 10, Foreground = new SolidColorBrush(Color.Parse("#8b93a1")), TextTrimming = TextTrimming.CharacterEllipsis });
        var volts = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Menlo, Consolas, monospace"), Margin = new Thickness(6, 0) };
        var seg = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        ToggleButton Btn(string text, string cls, PinDrive drive, CornerRadius cr)
        {
            var b = new ToggleButton { Content = text, Classes = { "seg", cls }, CornerRadius = cr };
            b.Click += (_, _) =>
            {
                if (_emu is not null && pin.Part == _emu.Mcu) { Status.Text = "The firmware drives the chip's pins while it runs."; RunSimulation(); return; }
                if (_emu is not null) { _emu.Read(_ => { _sim!.SetPin(pin, drive); return 0; }); _emu.Rerun(); return; }
                _sim!.SetPin(pin, drive);
                RunSimulation();
            };
            seg.Children.Add(b);
            return b;
        }
        var bi = Btn("In", "in", PinDrive.Input, new CornerRadius(6, 0, 0, 6));
        var bh = Btn("H", "hi", PinDrive.High, new CornerRadius(0));
        var bl = Btn("L", "lo", PinDrive.Low, new CornerRadius(0, 6, 6, 0));
        Grid.SetColumn(volts, 1);
        Grid.SetColumn(seg, 2);
        g.Children.Add(name); g.Children.Add(volts); g.Children.Add(seg);
        _pinRows[pin] = (volts, bi, bh, bl);
        return g;
    }

    private string StatusLine() => _report is null ? "Ready" :
        _report.Errors + _report.Warnings == 0 ? "No problems found" :
        $"{_report.Errors} errors and {_report.Warnings} warnings – click a finding to see it on the board";

    private void SelectFinding(FindingItem? item)
    {
        foreach (var f in _findings) f.IsSelected = f == item;
        if (item is null) { Board.ClearHighlight(); return; }
        var f2 = item.Finding;
        Board.Highlight(f2.Nets, f2.Refs, f2.Location, f2.Severity);
        if (f2.Location is { } loc) Board.FocusOn(loc);
    }

    private void OnPadClicked(Pad? pad)
    {
        if (pad is null || _report is null) { Board.ClearHighlight(); return; }
        if (ProbeButton.IsChecked == true && pad.HasNet && _sim?.Circuit.Nets.TryGetValue(pad.Net, out var probeNet) == true)
        {
            ToggleProbe(probeNet, pad);
            return;
        }
        if (Board.Simulation is not null && _sim is not null &&
            _sim.ControllablePins.FirstOrDefault(p => p.Pad == pad || (p.Part.Reference == pad.Footprint.Reference && p.Number == pad.Number)) is { } cpin)
        {
            var next = _sim.DriveOf(cpin) switch { PinDrive.Input => PinDrive.High, PinDrive.High => PinDrive.Low, _ => PinDrive.Input };
            _sim.SetPin(cpin, next);
            RunSimulation();
            Status.Text = $"{cpin.Label} → {next}. " + Status.Text;
            return;
        }
        Board.Highlight(pad.HasNet ? new[] { pad.Net } : Array.Empty<string>(), new[] { pad.Id });
        if (_sim?.Circuit.Part(pad.Footprint.Reference) is { Kind: PartKind.Crystal } xtal) { ShowCrystal(xtal); return; }
        ShowInspector(pad);
        Tabs.SelectedIndex = 1;
    }

    private string Describe(Pad pad)
    {
        var pin = _report?.Circuit.Part(pad.Footprint.Reference)?.Pin(pad.Number);
        var name = pin?.Label ?? pad.Id;
        var net = pad.HasNet ? pad.Net.TrimStart('/') : "no net";
        var volts = pin?.Net.Volts is { } v ? $" · {v:0.##} V" : "";
        if (Board.Simulation is not null && pin is not null) volts = " · " + VoltsText(pin.Net);
        return $"{name} · {pad.Footprint.Value} · {net}{volts}";
    }

    private void ShowInspector(Pad pad)
    {
        Inspector.Children.Clear();
        var part = _report!.Circuit.Part(pad.Footprint.Reference);
        var pin = part?.Pin(pad.Number);

        Inspector.Children.Add(Card(
            Heading(pin?.Label ?? pad.Id),
            Line("Part", $"{pad.Footprint.Reference} · {pad.Footprint.Value}"),
            Line("Model", part?.Model?.Name ?? "none – only generic checks"),
            Line("Pin type", pin?.Model?.Type.ToString() ?? "unknown"),
            Line("Net", pad.HasNet ? pad.Net.TrimStart('/') : "not connected"),
            Line("Voltage", pin?.Net.Volts is { } v ? $"{v:0.##} V ({pin.Net.VoltsSource})" : "signal (not a rail)"),
            pin?.Model?.Why is { Length: > 0 } why ? Note(why) : null));

        if (pin is not null)
        {
            var others = pin.Net.Pins.Where(p => p != pin).OrderBy(p => p.Part.Reference).ToList();
            var list = new StackPanel { Spacing = 4 };
            foreach (var other in others.Take(80))
            {
                var row = new Button
                {
                    Classes = { "tool" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = new TextBlock { Text = $"{other.Label}   {other.Part.Footprint.Value}", FontSize = 12 },
                };
                var target = other;
                row.Click += (_, _) =>
                {
                    Board.Highlight(new[] { target.Net.Name }, new[] { target.Id }, target.Pad.Position);
                    Board.FocusOn(target.Pad.Position);
                };
                list.Children.Add(row);
            }
            Inspector.Children.Add(Card(Heading($"On {pin.Net.DisplayName} ({others.Count} more)"),
                others.Count == 0 ? Note("Nothing else is on this net.") : list));

            var related = _report.Findings.Where(f => f.Refs.Contains(pin.Id) || f.Nets.Contains(pin.Net.Name)).ToList();
            if (related.Count > 0)
                Inspector.Children.Add(Card(new Control[] { Heading("Findings here") }
                    .Concat(related.Select(f => (Control)new TextBlock
                    {
                        Text = "• " + f.Title, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                        Foreground = FindingItem.BrushFor(f.Severity),
                    })).ToArray()));
        }
    }

    private void ToggleProbe(Net net, Pad pad)
    {
        var existing = _probes.FirstOrDefault(p => p.Net == net);
        if (existing is not null) { _probes.Remove(existing); _history.Remove(net); }
        else if (_probes.Count >= 4) { Status.Text = "All four channels are in use. Click a probed net to free one."; return; }
        else
        {
            var channel = Enumerable.Range(1, 4).First(c => _probes.All(p => p.Channel != c));
            _probes.Add(new Probe(channel, net, pad));
            _probes.Sort((a, b) => a.Channel.CompareTo(b.Channel));
            Status.Text = $"Channel {channel} on {net.DisplayName}";
        }
        ScopePanel.IsVisible = true;
        UpdateScope();
    }

    private void SetScopeMode(ScopeMode mode)
    {
        Scope.Mode = mode;
        ScopeRoll.IsChecked = mode == ScopeMode.Roll;
        ScopeRun.IsChecked = mode == ScopeMode.Run;
        UpdateScope();
    }

    /// <summary>
    /// What a probe on <paramref name="net"/> sees: the firmware's GPIO edges or
    /// LED data reaching it, a crystal's oscillation, or (null) a steady level.
    /// </summary>
    private ScopeTrace? TraceFor(Net net)
    {
        if (_emu?.TraceFor(net) is { } fromChip) return fromChip;
        if (_sim is null) return null;
        var c = _sim.Circuit;
        foreach (var xtal in c.Parts.Where(p => p.Kind == PartKind.Crystal))
        {
            var nets = xtal.Pins.Select(p => p.Net).Where(n => !n.IsRail).SelectMany(n => c.SeriesNets(n, 1)).ToHashSet();
            if (!nets.Contains(net) || _sim.CrystalHz(xtal) is not { } hz) continue;
            // An oscillator swings a few hundred millivolts around its bias.
            return new SineTrace(hz, 0.35, 1.15) { Source = $"{xtal.Reference} crystal" };
        }
        return null;
    }

    private void SetTrigMode(TrigMode mode)
    {
        _trigMode = mode;
        TrigAuto.IsChecked = mode == TrigMode.Auto;
        TrigNormal.IsChecked = mode == TrigMode.Normal;
        TrigSingle.IsChecked = mode == TrigMode.Single;
        if (mode == TrigMode.Auto)
        {
            _armed = false;
            _frozen = null;
            Scope.TriggerAtNs = null;
            Scope.RollNow = null;
            Scope.State = null;
            ScopeHold.IsChecked = false;
        }
        else Arm();
        Scope.InvalidateVisual();
    }

    /// <summary>Waits for the next trigger event from now on (chip time and wall clock).</summary>
    private void Arm()
    {
        if (Scope.TriggerChannel == 0 || _probes.All(p => p.Channel != Scope.TriggerChannel))
            Scope.TriggerChannel = _probes.OrderBy(p => p.Channel).FirstOrDefault()?.Channel ?? 0;
        _armNs = _emu?.Read(e => e.ChipTimeNs) ?? 0;
        _armWall = Environment.TickCount64 / 1000.0;
        _armed = true;
        ScopeHold.IsChecked = false;
        if (_trigMode == TrigMode.Single) { _frozen = null; Scope.TriggerAtNs = null; Scope.RollNow = null; }
        Scope.State = Scope.TriggerChannel == 0 ? "Waiting · clip on a probe" : $"Armed · CH{Scope.TriggerChannel} {EdgeName()}";
    }

    private string EdgeName() => Scope.Edge switch { TriggerEdge.Rising => "↑", TriggerEdge.Falling => "↓", _ => "↕" };

    /// <summary>Checks for the trigger event; once the screen after it is filled, captures all channels.</summary>
    private void TriggerStep()
    {
        var probe = _probes.FirstOrDefault(p => p.Channel == Scope.TriggerChannel);
        if (probe is null) { Scope.State = "Waiting · clip on a probe"; return; }
        var window = Scope.PerDiv * 10;
        string when;
        if (Scope.Mode == ScopeMode.Run)
        {
            var trace = TraceFor(probe.Net);
            if (Trigger.FindEvent(trace, _armNs, Scope.Edge) is not { } ev) return;
            if (!Trigger.WindowFilled(trace, ev, window * 1e9)) return;
            _frozen = _probes.ToDictionary(p => p.Channel, p => TraceFor(p.Net));
            Scope.TriggerAtNs = ev;
            _armNs = ev + (long)(window * 1e9 * 0.9);         // Normal: the next capture comes after this screen
            when = $"chip time {ScopeView.FormatTime(ev / 1e9)}";
        }
        else
        {
            if (!_history.TryGetValue(probe.Net, out var h) || Trigger.FindEvent(h, _armWall, Scope.Edge) is not { } ev) return;
            var now = Environment.TickCount64 / 1000.0;
            if (now < ev + window * 0.9) return;
            Scope.RollNow = ev + window * 0.9;
            _armWall = ev + window * 0.9;
            when = DateTime.Now.AddSeconds(ev - now).ToString("HH:mm:ss.fff");
        }
        Scope.State = $"Triggered · CH{probe.Channel} {EdgeName()} · {when}";
        if (_trigMode == TrigMode.Single)
        {
            _armed = false;
            ScopeHold.IsChecked = true;
            Status.Text = $"Single capture on CH{probe.Channel} {probe.Net.DisplayName} ({when}). Press Single again to re-arm.";
        }
        Scope.InvalidateVisual();
    }

    /// <summary>AUTO: a time base showing about four periods of the probe's signal, triggered on it.</summary>
    private void AutoScope(Probe? probe)
    {
        var candidates = probe is not null ? new[] { probe } : _probes.OrderBy(p => p.Channel).ToArray();
        foreach (var p in candidates)
        {
            var m = Measurement.Of(TraceFor(p.Net) ?? new DcTrace(null));
            if (m.AutoPerDivSeconds() is not { } perDiv) continue;
            SetScopeMode(ScopeMode.Run);
            Scope.SetPerDiv(perDiv);
            Scope.TriggerChannel = p.Channel;
            if (_trigMode != TrigMode.Auto) Arm();
            UpdateScope();
            Status.Text = $"CH{p.Channel} {p.Net.DisplayName}: {ScopeView.FormatHz(m.Hz!.Value)} - {ScopeView.FormatTime(perDiv)}/div, triggered on CH{p.Channel}";
            return;
        }
        Status.Text = probe is null
            ? "No probe sees a periodic signal yet (power on the chip, or probe a crystal or a pin the firmware toggles)."
            : $"CH{probe.Channel} {probe.Net.DisplayName} is steady - nothing to time.";
    }

    private async Task AutoSoon()
    {
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500);
            if (_probes.Any(p => Measurement.Of(TraceFor(p.Net) ?? new DcTrace(null)).Hz is not null)) { AutoScope(null); return; }
        }
    }

    private void UpdateScope()
    {
        Scope.Probes = _probes.ToList();
        Board.Probes = Scope.Probes;
        ScopeTime.Text = $"{ScopeView.FormatTime(Scope.PerDiv)}/div";
        Board.InvalidateVisual();
        Scope.InvalidateVisual();
    }

    /// <summary>Appends the probed nets' voltages to their history (seconds, volts; NaN = floating).</summary>
    private void SampleProbes()
    {
        if (_sim is null || _probes.Count == 0) return;
        var t = Environment.TickCount64 / 1000.0;
        foreach (var p in _probes)
        {
            var v = _sim.Nets.TryGetValue(p.Net, out var r) && r.Volts is { } volts ? volts : double.NaN;
            if (!_history.TryGetValue(p.Net, out var h)) _history[p.Net] = h = new List<(double, double)>();
            var changed = h.Count == 0 || !(h[^1].V.Equals(v) || Math.Abs(h[^1].V - v) < 0.005);
            if (changed || t - h[^1].T > 0.25) h.Add((t, v));
            if (h.Count > 4000) h.RemoveRange(0, 1000);
        }
    }

    /// <summary>Inspector for a crystal: its frequency can be changed to see what the chip makes of it.</summary>
    private void ShowCrystal(Part xtal)
    {
        if (_sim is null || _report is null) return;
        Inspector.Children.Clear();
        Tabs.SelectedIndex = 1;
        var chip = _report.Circuit.Parts.FirstOrDefault(p => p.Model?.CrystalHz is not null && _report.Circuit.CrystalOf(p) == xtal);
        var expected = chip?.Model?.CrystalHz;
        var hz = _sim.CrystalHz(xtal) ?? 0;

        var big = new TextBlock { Text = $"{hz / 1e6:0.###} MHz", FontSize = 30, FontWeight = FontWeight.Bold };
        var effect = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void Describe()
        {
            var now = _sim.CrystalHz(xtal) ?? 0;
            big.Text = $"{now / 1e6:0.###} MHz";
            if (chip is null || expected is null) { effect.Text = "No chip on this board says which frequency it needs."; return; }
            var ratio = now / expected.Value;
            big.Foreground = new SolidColorBrush(Color.Parse(Math.Abs(ratio - 1) < 1e-6 ? "#41d98a" : Math.Abs(ratio - 1) <= 0.04 ? "#ffb547" : "#ff5c5c"));
            effect.Text = Math.Abs(ratio - 1) < 1e-6
                ? $"{chip.Reference} expects {expected / 1e6:0} MHz: every clock is right."
                : $"{chip.Reference} expects {expected / 1e6:0} MHz, so all its clocks run at {ratio * 100:0} %. " +
                  $"UART 115200 baud comes out as {115200 * ratio:0} baud" +
                  (Math.Abs(ratio - 1) > 0.04 ? " - too far off: the console shows garbage and idf.py flash cannot connect. " : ". ") +
                  $"A WS2812 0-bit is {0.3 / ratio:0.00} µs high instead of 0.30 µs, a 1-bit {0.9 / ratio:0.00} µs instead of 0.90 µs" +
                  (0.3 / ratio > 0.6 || 0.9 / ratio < 0.6 ? " - the LEDs can no longer tell them apart." : ".");
        }
        void Set(double? value)
        {
            if (_emu is not null) _emu.Read(_ => { _sim.SetCrystalHz(xtal, value); return 0; });
            else _sim.SetCrystalHz(xtal, value);
            Describe();
            Board.InvalidateVisual();
        }

        var presets = new WrapPanel();
        foreach (var mhz in new[] { 16.0, 26, 32, 38, 40, 42, 48 })
        {
            var b = new Button { Classes = { "tool" }, Content = $"{mhz:0} MHz", Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4) };
            b.Click += (_, _) => Set(mhz * 1e6);
            presets.Children.Add(b);
        }
        var custom = new TextBox { Watermark = "Custom, in MHz (e.g. 39.5)", Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        custom.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && double.TryParse(custom.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                Set(v * 1e6);
        };
        var reset = new Button { Classes = { "tool" }, Content = $"Back to the board's {xtal.Footprint.Value}" };
        reset.Click += (_, _) => Set(null);

        Describe();
        Inspector.Children.Add(Card(Heading($"{xtal.Reference} · crystal"), big, effect));
        Inspector.Children.Add(Card(Heading("Try another frequency"), presets, custom, reset,
            Note("This changes the simulation only, not the board file. Power on the chip to see the effect on the firmware: the LED strings and the Console.")));
    }

    private static Border Card(params Control?[] children)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var c in children) if (c is not null) panel.Children.Add(c);
        return new Border { Background = new SolidColorBrush(Color.Parse("#1b2029")), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), Child = panel };
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Note(string text) => new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#8b93a1")) };

    private static Grid Line(string label, string value)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*") };
        g.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8b93a1")) });
        var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    private async Task SaveScreenshot(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var scale = RenderScaling;
        var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bmp.Render(this);
        bmp.Save(path);
    }
}

public sealed class FindingItem(Finding finding) : INotifyPropertyChanged
{
    private bool _selected;
    public Finding Finding { get; } = finding;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string Rule => $"{Finding.Severity.ToString().ToLowerInvariant()} · {Finding.Rule}";
    public IBrush Color => BrushFor(Finding.Severity);

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static IBrush BrushFor(Severity s) => new SolidColorBrush(Avalonia.Media.Color.Parse(s switch
    {
        Severity.Error => "#ff5c5c",
        Severity.Warning => "#ffb547",
        _ => "#6ca0ff",
    }));
}

public sealed class PartItem(Part part, ComponentInfo? info)
{
    public string Reference => part.Reference;
    public string Value => part.Footprint.Value;
    public string Kind => part.Kind.ToString() + (info?.Mpn is { } mpn ? $" · {mpn}" : "");
    public string ModelLabel => part.Model?.Id is { } id ? (part.Model.Source?.StartsWith("project:") == true ? id + " (project)" : id) : "generic";
    public IBrush ModelBackground => new SolidColorBrush(Color.Parse(part.Model is null ? "#222833" : "#12384a"));
    public IBrush ModelForeground => new SolidColorBrush(Color.Parse(part.Model is null ? "#8b93a1" : "#7fe0ff"));

    public string Model3DLabel => info?.Model3D switch
    {
        null => info is null ? "3D …" : "no 3D model",
        { Source: DataSource.Missing } => "3D missing",
        { } m => $"3D {m.Source switch { DataSource.Project => "project", DataSource.KiCadLibrary => "KiCad", _ => "library" }}" +
                 (m.HeightMm is { } h ? $" · {h:0.#} mm" : ""),
    };
    public string Model3DFile => info?.Model3D?.File ?? info?.Model3D?.Model.Path ?? "";
    public IBrush Model3DBackground => new SolidColorBrush(Color.Parse(info?.Model3D?.Source switch
    {
        DataSource.Project => "#14331f", DataSource.KiCadLibrary or DataSource.ThirdParty => "#1b2733", DataSource.Missing => "#3a1c1c", _ => "#222833",
    }));
    public IBrush Model3DForeground => new SolidColorBrush(Color.Parse(info?.Model3D?.Source switch
    {
        DataSource.Project => "#7ee2a0", DataSource.KiCadLibrary or DataSource.ThirdParty => "#9fb4c8", DataSource.Missing => "#ff8a8a", _ => "#8b93a1",
    }));
}

/// <summary>One switching net in the EMC tab: its signal, route and emission margin.</summary>
public sealed class EmcNetItem
{
    public ActiveNet Net { get; }
    public double Margin { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Data { get; }

    public EmcNetItem(EmcAnalysis emc, ActiveNet a)
    {
        Net = a;
        var peak = emc.Peak(a);
        Margin = peak.Margin;
        var hz = a.Signal.Hz >= 1e6 ? $"{a.Signal.Hz / 1e6:0.##} MHz" : $"{a.Signal.Hz / 1e3:0.#} kHz";
        Title = $"{a.Net.DisplayName} · {a.Signal.Name}";
        var r = a.Route;
        Detail = $"{hz}, {(a.Signal.Sine ? "sine" : $"{a.EdgeNs:0.#} ns edges")}, {a.CurrentA * 1000:0} mA · {r.Length:0} mm, {r.Vias} vias, loop {r.LoopArea:0.#} mm²" +
                 (r.Unreferenced > 0.5 ? $", {r.Unreferenced:0} mm without plane" : "") +
                 (a.LeavesBoard ? $" · cable on {string.Join(", ", a.Exits.Select(e => e.Part.Reference).Distinct())}" : "") +
                 (double.IsNegativeInfinity(peak.Level) ? "" : $" · peak {peak.Level:0} dBµV/m at {peak.Hz / 1e6:0} MHz");
        Data = "Edge data: " + a.Signal.Data;
    }

    public string MarginLabel => double.IsPositiveInfinity(Margin) || Margin > 60 ? "quiet" : Margin < 0 ? $"{-Margin:0} dB over" : $"{Margin:0} dB margin";
    public IBrush MarginBackground => new SolidColorBrush(Color.Parse(Margin < 0 ? "#3a1c1c" : Margin < 6 ? "#3a2f12" : "#14331f"));
    public IBrush MarginForeground => new SolidColorBrush(Color.Parse(Margin < 0 ? "#ff8a8a" : Margin < 6 ? "#ffd166" : "#7ee2a0"));
}
