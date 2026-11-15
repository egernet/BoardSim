using BoardSim.Core.Analysis;
using BoardSim.Core.Components;
using BoardSim.Core.Emc;
using BoardSim.Core.Emulation;
using BoardSim.Core.Model;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;

namespace BoardSim.Tests;

/// <summary>
/// The noise estimates against the hand-made noise analysis of the same board
/// (julelys_pcb docs/noise-analysis-test-board.md, "as ordered").
/// </summary>
public class EmcTests
{
    private static readonly Lazy<(Circuit Circuit, EmcAnalysis Emc)> V4 = new(() =>
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var exp = Expectations.Load(Path.Combine(Fixtures.Dir, "julelys_v4.boardsim.json"));
        return (circuit, new EmcAnalysis(circuit, exp, new EmcOptions()));
    });

    private static ActiveNet Net(string name) => V4.Value.Emc.Active.Single(a => a.Net.DisplayName == name);

    [Fact]
    public void ReadsTheStackupFromTheBoard()
    {
        var st = V4.Value.Emc.Routes.Stackup;
        Assert.True(st.FromFile);
        Assert.InRange(st.Gap("F.Cu", "In1.Cu"), 0.24, 0.25);     // 0.035 copper + 0.2104 prepreg (JLC04161H-7628)
        Assert.InRange(st.Thickness, 1.5, 1.65);
        var plane = V4.Value.Emc.Routes.Planes.Planes().Single(p => p.Layer == "In2.Cu");
        Assert.Equal("+5V", plane.Net);
        Assert.InRange(plane.Area, 2500, 2620);                     // the analysis measured 2560 mm²
    }

    [Fact]
    public void MeasuresTheUsbPairLikeTheNoiseAnalysis()
    {
        var plus = Net("USB_D+").Route;
        var minus = Net("USB_D-").Route;
        Assert.InRange(plus.Length, 61, 63);   // 62 mm, 6 vias
        Assert.Equal(6, plus.Vias);
        Assert.InRange(minus.Length, 72, 74);  // 73 mm, 8 vias
        Assert.Equal(8, minus.Vias);
        Assert.Equal(12e6, Net("USB_D+").Signal.Hz);
        Assert.Equal(4, Net("USB_D+").Signal.RiseNs);   // USB full speed: from the ESP32-C3 part model's USB pins
    }

    [Fact]
    public void LedDataFollowsTheBufferAndTheMux()
    {
        // From boardsim.json: GPIO7 at 800 kHz, through U6 (74AHCT1G125, new edge at 5 V) and U2 (mux, one output at a time).
        var io7 = Net("IO7");
        Assert.Contains("ESP32-C3", io7.Signal.Data);
        var data = Net("Data");
        Assert.Equal(5, data.Signal.Volts);
        Assert.Contains("74AHCT1G125", data.Signal.Data);
        var channels = V4.Value.Emc.Active.Where(a => a.Net.DisplayName.StartsWith("CH")).ToList();
        Assert.Equal(8, channels.Count);
        Assert.All(channels, c => Assert.Equal("U2 outputs", c.Signal.Group));
        // The 330 Ω series resistors slow the cable edges (the analysis: "about 400 Ω source impedance").
        Assert.All(channels, c => Assert.True(c.EdgeNs > 10, $"{c.Net.DisplayName}: {c.EdgeNs} ns"));
        Assert.All(channels, c => Assert.True(c.LeavesBoard));
    }

    [Fact]
    public void SeriesResistorOnTxSlowsItsEdges()
    {
        var tx = Net("TXD");
        Assert.True(tx.EdgeNs > 10);     // R42 510 Ω after the ESP32 pin
    }

    [Fact]
    public void FindsTheTvsDistancesOfTheNoiseAnalysis()
    {
        var findings = new EsdDistanceRule().Check(new CheckContext { Circuit = V4.Value.Circuit }).ToList();
        var u12 = Assert.Single(findings, f => f.Title.StartsWith("U12"));
        Assert.Contains("13-22 mm", u12.Title);    // the analysis: TVS 7-22 mm from the JSTs
    }

    [Fact]
    public void EmissionIsBelowTheLimitAndCableModeDominatesUsb()
    {
        var emc = V4.Value.Emc;
        var usb = Net("USB_D+");
        var (hz, level, margin) = emc.Peak(usb);
        Assert.InRange(hz, 30e6, 1e9);
        Assert.True(margin > -10 && margin < 20, $"margin {margin}");
        Assert.True(emc.CommonMode(usb, hz) > emc.DifferentialMode(usb, hz));
        // The crystal is a sine: nothing at other frequencies.
        Assert.True(double.IsNegativeInfinity(emc.DifferentialMode(Net("ESP32/XTAL_N"), 100e6)));
    }

    [Fact]
    public void ShortTracksBarelyCrosstalk()
    {
        // At 3 ns the coupled noise only saturates after about 22 cm; on a 65 mm board it stays well below 1 %.
        Assert.All(V4.Value.Emc.Crosstalk(), x => Assert.True(x.Percent < 1));
    }

    [Fact]
    public void ResolvesProjectModelsAndReadsTheirHeight()
    {
        var dir = Directory.CreateTempSubdirectory("boardsim-3d").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "3D"));
            File.WriteAllText(Path.Combine(dir, "3D", "My_Part.step"),
                "ISO-10303-21;\n#1=CARTESIAN_POINT('',(0.,0.,0.));\n#2=CARTESIAN_POINT('',(1.,2.,4.5));\nEND-ISO-10303-21;\n");
            var resolver = new ModelResolver(dir);
            var inProject = resolver.Resolve(new Model3D("${KIPRJMOD}/3D/My_Part.step", new double[3], new[] { 1.0, 1, 1 }, new double[3], false));
            Assert.Equal(DataSource.Project, inProject.Source);
            Assert.Equal(4.5, inProject.HeightMm);
            // An absolute path from another machine: found by name in the project folder instead.
            var moved = resolver.Resolve(new Model3D("/Users/someone/Downloads/My Part.stp", new double[3], new[] { 1.0, 1, 1 }, new[] { 0, 0, 0.0 }, false));
            Assert.Equal(DataSource.Project, moved.Source);
            Assert.Equal(DataSource.Missing, resolver.Resolve(new Model3D("${KIPRJMOD}/nothing_like_it.step", new double[3], new[] { 1.0, 1, 1 }, new double[3], false)).Source);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScopeKeepsEveryChannelsFrames()
    {
        var circuit = Circuit.Build(Fixtures.V4, PartLibrary.BuiltIn());
        var board = new EmulatedBoard(new Simulator(circuit), EmulatedBoard.FindMcu(circuit)!, "RGB");
        var frame = (BridgeMessage.Rmt)BridgeMessage.Parse(File.ReadAllText(Path.Combine(Fixtures.Dir, "rmt_frame_julelys_rain.txt")).Trim())!;
        board.Handle(new BridgeMessage.Route(7, 51));
        // Channel 1 (CH2), then channel 4 (CH5), then channel 1 again.
        board.Handle(new BridgeMessage.Pins(0x10, 0x1b0, 1_000));
        board.Handle(frame with { TimeNs = 10_000_000 });
        board.Handle(new BridgeMessage.Pins(0x100, 0x1b0, 11_000_000));
        board.Handle(frame with { TimeNs = 20_000_000 });
        board.Handle(new BridgeMessage.Pins(0x10, 0x1b0, 21_000_000));
        board.Handle(frame with { TimeNs = 30_000_000 });

        var ch2 = Assert.IsType<RmtTrace>(board.TraceFor(circuit.Part("J5")!.Pin("2")!.Net));
        var ch5 = Assert.IsType<RmtTrace>(board.TraceFor(circuit.Part("J8")!.Pin("2")!.Net));
        Assert.Equal(new long?[] { 10_000_000, 30_000_000 }, ch2.Frames.Select(f => f.EndNs));
        Assert.Equal(new long?[] { 20_000_000 }, ch5.Frames.Select(f => f.EndNs));
        // The trigger finds the next frame on its own channel.
        Assert.Equal(ch2.Frames[1].StartNs, Trigger.FindEvent(ch2, 12_000_000, TriggerEdge.Rising));
    }

    [Fact]
    public void WithoutKiCadPartsBecomeBoxesAtTheirHeight()
    {
        var circuit = V4.Value.Circuit;
        var parts = circuit.Parts.Select(p => new ComponentInfo(p, p.Model?.Id, false,
            p.Reference == "C15" ? new ResolvedModel(new Model3D("x.step", new double[3], new[] { 1.0, 1, 1 }, new double[3], false), "x.step", DataSource.Project, 10.5) : null, null)).ToList();
        var mesh = Board3D.Boxes(circuit, parts, "boxes");
        Assert.InRange(mesh.Max.Z, 10.49f, 10.51f);                // the tallest part is C15 at its model's height
        Assert.InRange(mesh.Max.X - mesh.Min.X, 64, 66);    // 65 mm wide board
        Assert.True(mesh.Triangles > 100);
    }

    [Fact]
    public void FindsModelsBesideTheBoardFolderAndEmbeddedOnes()
    {
        // Project/Hardware/board.kicad_pcb with the models in Project/Libraries/3D models, named by a Windows path.
        var root = Directory.CreateTempSubdirectory("boardsim-proj").FullName;
        try
        {
            var hw = Directory.CreateDirectory(Path.Combine(root, "Hardware")).FullName;
            var models = Directory.CreateDirectory(Path.Combine(root, "Libraries", "3D models")).FullName;
            File.WriteAllText(Path.Combine(models, "BME280.step"), "#1=CARTESIAN_POINT('',(0.,0.,0.93));");
            var resolver = new ModelResolver(hw);
            var m = resolver.Resolve(new Model3D("C:/Users/someone/OneDrive/Kicad/3D models ESP32/BME280.step", new double[3], new[] { 1.0, 1, 1 }, new double[3], false));
            Assert.Equal(DataSource.Project, m.Source);
            Assert.Equal(0.93, m.HeightMm);
            Assert.Equal(DataSource.Embedded, resolver.Resolve(new Model3D("kicad-embed://GSD090012SEU.step", new double[3], new[] { 1.0, 1, 1 }, new double[3], false)).Source);
            Assert.Contains(root, ModelResolver.ProjectRoots(hw));
        }
        finally { Directory.Delete(root, true); }
    }
}
