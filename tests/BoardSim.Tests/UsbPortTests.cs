using System.Net.Sockets;
using BoardSim.Core.Emulation;

namespace BoardSim.Tests;

public class UsbPortTests
{
    private sealed class Recorder
    {
        public readonly List<string> Events = new();
        public AutoReset Reset() => new(
            hold: _ => { lock (Events) Events.Add("hold"); },
            boot: dl => { lock (Events) Events.Add(dl ? "download" : "flash"); })
        { FlashBootAfterMs = 150 };
    }

    [Fact]
    public async Task EsptoolResetOverRfc2217BootsDownloadMode()
    {
        // esptool's ClassicReset, with pyserial's RFC 2217 round trips (~60 ms) between the line changes.
        var r = new Recorder();
        var reset = r.Reset();
        reset.Update(dtr: true, rts: false);   // port opened
        reset.Update(dtr: true, rts: true);
        reset.Update(dtr: false, rts: true);   // EN low
        await Task.Delay(100);
        reset.Update(dtr: true, rts: true);    // transient: both asserted
        await Task.Delay(60);
        reset.Update(dtr: true, rts: false);   // GPIO9 low, EN high
        await Task.Delay(60);
        reset.Update(dtr: false, rts: false);
        await Task.Delay(250);
        Assert.Equal(new[] { "hold", "download" }, r.Events);
    }

    [Fact]
    public async Task HardResetAfterFlashingBootsFromFlash()
    {
        var r = new Recorder();
        var reset = r.Reset();
        reset.Update(dtr: false, rts: true);    // esptool --after hard_reset
        await Task.Delay(100);
        reset.Update(dtr: false, rts: false);
        await Task.Delay(250);
        Assert.Equal(new[] { "hold", "flash" }, r.Events);
    }

    [Fact]
    public async Task OpeningThePortDoesNotResetTheChip()
    {
        var r = new Recorder();
        var reset = r.Reset();
        reset.Update(dtr: true, rts: true);     // pyserial asserts both on open
        reset.Update(dtr: false, rts: false);
        await Task.Delay(250);
        Assert.Empty(r.Events);
    }

    [Fact]
    public async Task Rfc2217NegotiatesAndCarriesDataAndControlLines()
    {
        using var server = new Rfc2217Server(0);
        var received = new List<byte>();
        var lines = new List<(bool, bool)>();
        server.Data += d => { lock (received) received.AddRange(d); };
        server.ControlLines += (dtr, rts) => { lock (lines) lines.Add((dtr, rts)); };

        using var client = new TcpClient("127.0.0.1", server.Port);
        var s = client.GetStream();
        const byte IAC = 255, WILL = 251, DO = 253, SB = 250, SE = 240;
        // WILL COM-PORT-OPTION, then SET-CONTROL DTR ON, then data with an escaped 0xFF.
        s.Write(new byte[] { IAC, WILL, 44 });
        s.Write(new byte[] { IAC, SB, 44, 5, 8, IAC, SE });
        s.Write(new byte[] { 0xC0, IAC, IAC, 0x01 });

        var reply = new byte[64];
        var got = new List<byte>();
        s.ReadTimeout = 2000;
        while (got.Count < 10) got.AddRange(reply[..s.Read(reply)]);
        Assert.Equal(new byte[] { IAC, DO, 44 }, got.Take(3));                          // agrees to RFC 2217
        Assert.Equal(new byte[] { IAC, SB, 44, 105, 8, IAC, SE }, got.Skip(3).Take(7)); // acknowledges DTR ON

        await Task.Delay(100);
        lock (received) Assert.Equal(new byte[] { 0xC0, 0xFF, 0x01 }, received);
        lock (lines) Assert.Equal((true, false), Assert.Single(lines));

        // Chip to host: 0xFF is doubled on the wire.
        server.Write(new byte[] { 0x01, 0xFF });
        var n = s.Read(reply);
        Assert.Equal(new byte[] { 0x01, IAC, IAC }, reply[..n]);
    }

    [Fact]
    public void BuildsAFlashImageFromAnEspIdfBuildFolder()
    {
        var dir = Directory.CreateTempSubdirectory("boardsim-build").FullName;
        File.WriteAllText(Path.Combine(dir, "flash_args"), "--flash_mode dio --flash_size 4MB\n0x0 boot.bin\n0x10000 app.bin\n");
        File.WriteAllBytes(Path.Combine(dir, "boot.bin"), new byte[] { 0xE9, 1, 2 });
        File.WriteAllBytes(Path.Combine(dir, "app.bin"), new byte[] { 0xE9, 9 });
        var image = Firmware.BuildImage(dir);
        Assert.Equal(4 << 20, image.Length);
        Assert.Equal(new byte[] { 0xE9, 1, 2, 0xFF }, image[..4]);
        Assert.Equal(new byte[] { 0xE9, 9, 0xFF }, image[0x10000..0x10003]);
    }
}
