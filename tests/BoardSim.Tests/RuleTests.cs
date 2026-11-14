using BoardSim.Core.Analysis;
using BoardSim.Core.Parts;
using Xunit.Abstractions;

namespace BoardSim.Tests;

public class RuleTests(ITestOutputHelper output)
{
    private static BoardReport Check(string file) => Checker.Run(Path.Combine(Fixtures.Dir, file));

    private void Dump(BoardReport r)
    {
        foreach (var f in r.Findings) output.WriteLine($"{f}\n    {f.Detail}");
    }

    [Fact]
    public void ModelsMatchTheJulelysParts()
    {
        var c = Check("julelys_v4.kicad_pcb").Circuit;
        Assert.Equal("esp32-c3-qfn32", c.Part("U1")!.Model?.Id);
        Assert.Equal("74hc4051", c.Part("U2")!.Model?.Id);
        Assert.Equal("ams1117-3.3", c.Part("U3")!.Model?.Id);
        Assert.Equal("raspberry-pi-40", c.Part("J1")!.Model?.Id);
        Assert.Equal("usb-c-receptacle-usb2", c.Part("P1")!.Model?.Id);
        Assert.Equal(PartKind.Led, c.Part("D1")!.Kind);
        Assert.Equal(PartKind.Tvs, c.Part("D10")!.Kind);
        Assert.Equal(3.3, c.Nets["+3.3V"].Volts);
        // VDD3P3 is fed through L1, so it inherits 3.3 V.
        Assert.Equal(3.3, c.Nets["/ESP32/VDD3P3"].Volts);
        Assert.Equal(5, c.Nets["VBUS"].Volts);
        Assert.Equal("esp32-c3-wroom-02", Check("julelys_v31.kicad_pcb").Circuit.Part("U1")!.Model?.Id);
    }

    [Fact]
    public void V4HasNoErrorsOrWarnings()
    {
        var r = Check("julelys_v4.kicad_pcb");
        Dump(r);
        Assert.NotNull(r.Expectations);
        Assert.Empty(r.Findings.Where(f => f.Severity != Severity.Info));
    }

    [Fact]
    public void V31ShowsTheProblemsV4Fixed()
    {
        var r = Check("julelys_v31.kicad_pcb");
        Dump(r);
        // GPIO8 drove the mux LED D3 through R3 to ground: download mode failed.
        Assert.Contains(r.Findings, f => f is { Rule: "strap-pin", Severity: Severity.Error } && f.Refs.Contains("U1.7") && f.Refs.Contains("D3"));
        // GPIO2 (strapping) had no pull-up: v4 added R35.
        Assert.Contains(r.Findings, f => f.Rule == "strap-pin" && f.Title.Contains("GPIO2"));
        // 100 nF on GPIO9: v4 uses 1 nF.
        Assert.Contains(r.Findings, f => f.Rule == "capacitance" && f.Title.Contains("GPIO9"));
        // No pull-downs on the mux outputs: v4 added R43-R50.
        Assert.Equal(8, r.Findings.Count(f => f.Rule == "floating-output"));
        Assert.DoesNotContain(r.Findings, f => f.Rule == "unrouted");
    }

    [Fact]
    public void FindsASwappedFirmwarePin()
    {
        var board = Fixtures.V4;
        var exp = Expectations.Load(Path.Combine(Fixtures.Dir, "julelys_v4.boardsim.json"));
        // Firmware that thinks bit 0 is on GPIO5 instead of GPIO4.
        exp.Connections.Single(c => c.From == "U1.GPIO4").From = "U1.GPIO5";
        var r = Checker.Run(board, PartLibrary.BuiltIn(), exp);
        var f = Assert.Single(r.Findings, f => f.Rule == "expected-connection");
        output.WriteLine($"{f}\n    {f.Detail}");
        Assert.Contains("U2.S0", f.Title);
        Assert.Contains("U2.S1", f.Detail);
    }

    [Fact]
    public void FindsFiveVoltOnAThreeVoltPin()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures.Dir, "julelys_v4.kicad_pcb"));
        // Move the GPIO2 pull-up R35 from +3.3V to +5V.
        var board = BoardSim.Core.KiCad.KiCadPcbLoader.Parse(text);
        var pad = board.Find("R35")!.Pads.Single(p => p.Net == "+3.3V");
        pad.Net = "+5V";
        var r = Checker.Run(board, PartLibrary.BuiltIn(), null);
        Dump(r);
        Assert.Contains(r.Findings, f => f is { Rule: "overvoltage", Severity: Severity.Error } && f.Title.Contains("GPIO2") && f.Refs.Contains("R35"));
    }

    [Theory]
    [InlineData("10K", 10e3)]
    [InlineData("4k7", 4.7e3)]
    [InlineData("5.1k", 5.1e3)]
    [InlineData("100nF", 100e-9)]
    [InlineData("1uf/16V", 1e-6)]
    [InlineData("2.0nH", 2e-9)]
    [InlineData("510", 510)]
    [InlineData("1.5pF", 1.5e-12)]
    public void ParsesValues(string text, double expected) =>
        Assert.Equal(expected, Units.Parse(text)!.Value, expected * 1e-9);

    [Theory]
    [InlineData("GND", 0.0)]
    [InlineData("+5V", 5.0)]
    [InlineData("+3.3V", 3.3)]
    [InlineData("3V3", 3.3)]
    [InlineData("/Power/VCC_3V3", 3.3)]
    [InlineData("VBUS", 5.0)]
    public void ReadsVoltageFromNetNames(string name, double volts) =>
        Assert.Equal(volts, Units.VoltsFromNetName(name));

    [Fact]
    public void LeavesSignalNetsWithoutVoltage()
    {
        Assert.Null(Units.VoltsFromNetName("IO7"));
        Assert.Null(Units.VoltsFromNetName("/CH1"));
        Assert.Null(Units.VoltsFromNetName("A0"));
    }
}
