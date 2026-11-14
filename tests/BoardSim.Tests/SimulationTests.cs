using BoardSim.Core.Analysis;
using BoardSim.Core.Parts;
using BoardSim.Core.Simulation;
using Xunit.Abstractions;

namespace BoardSim.Tests;

public class SimulationTests(ITestOutputHelper output)
{
    private static Simulator Sim(bool v4 = true) =>
        new(Circuit.Build(v4 ? Fixtures.V4 : Fixtures.V31, PartLibrary.BuiltIn()));

    private static PinRef Pin(Simulator s, string spec) => ExpectationRule.Resolve(s.Circuit, spec)!;
    private static NetResult Net(Simulator s, string name) => s.Nets[s.Circuit.Nets[name]];

    private void Dump(Simulator s, params string[] nets)
    {
        foreach (var n in nets) output.WriteLine($"{n}: {Net(s, n)}");
        foreach (var (led, i) in s.LedCurrent) output.WriteLine($"{led.Reference}: {i * 1000:0.00} mA");
    }

    [Fact]
    public void PowerLedsLightAtStartup()
    {
        var s = Sim();
        s.Run();
        Dump(s);
        // D5: +5V - 5.1k - green LED: (5 - 2.1) / 5.1k is about 0.57 mA.
        Assert.InRange(s.LedCurrent[s.Circuit.Part("D5")!], 0.4e-3, 0.7e-3);
        // D9: +3.3V - 1k - green LED: about 1.2 mA.
        Assert.InRange(s.LedCurrent[s.Circuit.Part("D9")!], 0.9e-3, 1.4e-3);
        // The mux LEDs are off while the select lines are low.
        Assert.Equal(0, s.LedCurrent[s.Circuit.Part("D1")!]);
    }

    [Fact]
    public void FloatingAndPulledNetsAtStartup()
    {
        var s = Sim();
        s.Run();
        // GPIO9 has an external pull-up (R30) and reads high.
        Assert.Equal(NetState.High, Net(s, "IO9").State);
        // U6 is enabled by R51; its input IO7 has a pull-down (R36), so Data is driven low.
        Assert.Equal(NetState.Low, Net(s, "/Data").State);
    }

    [Theory]
    [InlineData(false, false, false, "/CH1")]
    [InlineData(true, false, false, "/CH2")]
    [InlineData(true, false, true, "/CH6")]
    [InlineData(true, true, true, "/CH8")]
    public void MuxRoutesLedDataToTheSelectedChannel(bool b0, bool b1, bool b2, string channel)
    {
        var s = Sim();
        s.SetPin(Pin(s, "U1.GPIO4"), b0 ? PinDrive.High : PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO5"), b1 ? PinDrive.High : PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO8"), b2 ? PinDrive.High : PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO7"), PinDrive.High);
        s.Run();
        Dump(s, "/Data", "/AS0", "/AS1", "/AS2", channel, "/CH1", "/CH8");

        // 5 V from U6 through the mux (80 ohm) and 330 ohm into the 10k pull-down.
        Assert.InRange(Net(s, channel).Volts!.Value, 4.6, 5.0);
        foreach (var ch in Enumerable.Range(1, 8).Select(i => $"/CH{i}").Where(c => c != channel))
            Assert.Equal(NetState.Low, Net(s, ch).State);
        // The select LEDs D1-D3 show the channel number.
        Assert.Equal(b0, s.LedCurrent[s.Circuit.Part("D1")!] > 0);
        Assert.Equal(b2, s.LedCurrent[s.Circuit.Part("D3")!] > 0);
    }

    [Fact]
    public void UnselectedChannelsFloatOnV31()
    {
        var s = Sim(v4: false);
        s.SetPin(Pin(s, "U1.GPIO4"), PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO5"), PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO8"), PinDrive.Low);
        s.SetPin(Pin(s, "U1.GPIO7"), PinDrive.High);
        s.Run();
        Dump(s, "/CH1", "/CH2");
        Assert.Equal(NetState.High, Net(s, "/CH1").State);
        Assert.Equal(NetState.Floating, Net(s, "/CH2").State);
    }

    [Fact]
    public void SensorDividerScalesData()
    {
        var s = Sim();
        s.SetPin(Pin(s, "U1.GPIO7"), PinDrive.High);
        s.Run();
        Dump(s, "/Data", "IO0");
        // R52 15k / R53 10k: 5 V becomes 2 V on IO0 (the ESP32 ADC input).
        Assert.InRange(Net(s, "IO0").Volts!.Value, 1.9, 2.1);
    }

    [Fact]
    public void BootButtonPullsGpio9Low()
    {
        var s = Sim();
        s.SetPressed(s.Circuit.Part("SW1")!, true);
        s.Run();
        Assert.Equal(NetState.Low, Net(s, "IO9").State);
    }

    [Fact]
    public void TwoOutputsFightingIsAConflict()
    {
        var s = Sim();
        // The Pi drives MOSI high while the ESP32 drives the same line low.
        s.SetPin(Pin(s, "J1.19"), PinDrive.High, 3.3);
        s.SetPin(Pin(s, "U1.GPIO6"), PinDrive.Low);
        s.Run();
        Dump(s, "IO6");
        Assert.Equal(NetState.Conflict, Net(s, "IO6").State);
    }

    [Fact]
    public void Gpio8LedLoadOnV31()
    {
        // On v3.1 the mux LED D3 hangs on GPIO8 itself; driving it high lights the LED.
        var s = Sim(v4: false);
        s.SetPin(Pin(s, "U1.GPIO8"), PinDrive.High);
        s.Run();
        Assert.True(s.LedCurrent[s.Circuit.Part("D3")!] > 0.5e-3);
    }
}
