using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BoardSim.Core.Emulation;

/// <summary>One line from the BoardSim pin bridge in the QEMU fork.</summary>
public abstract record BridgeMessage
{
    public sealed record Reset : BridgeMessage;
    public sealed record Pins(uint Out, uint Enable) : BridgeMessage;
    public sealed record Route(int Gpio, int Signal) : BridgeMessage;
    public sealed record Rmt(int Gpio, uint TickPs, uint[] Items) : BridgeMessage;

    public static BridgeMessage? Parse(string line)
    {
        var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return null;
        try
        {
            switch (p[0])
            {
                case "B": return new Reset();
                case "O" when p.Length >= 3:
                    return new Pins(Convert.ToUInt32(p[1], 16), Convert.ToUInt32(p[2], 16));
                case "M" when p.Length >= 3:
                    return new Route(int.Parse(p[1], CultureInfo.InvariantCulture), int.Parse(p[2], CultureInfo.InvariantCulture));
                case "R" when p.Length >= 4:
                {
                    var count = int.Parse(p[3], CultureInfo.InvariantCulture);
                    var hex = p.Length > 4 ? p[4] : "";
                    var items = new uint[Math.Min(count, hex.Length / 8)];
                    for (var i = 0; i < items.Length; i++)
                        items[i] = uint.Parse(hex.AsSpan(i * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    return new Rmt(int.Parse(p[1], CultureInfo.InvariantCulture), uint.Parse(p[2], CultureInfo.InvariantCulture), items);
                }
            }
        }
        catch (FormatException) { }
        catch (OverflowException) { }
        return null;
    }
}

public sealed class QemuOptions
{
    public required string QemuPath { get; init; }
    public string? RomDir { get; init; }
    public required string FlashImage { get; init; }
    /// <summary>Host UDP port forwarded to the guest's UDP 2412 (the Julelys stream); null for none.</summary>
    public int? StreamPort { get; init; } = 12412;
    /// <summary>Boot into the ROM's UART download mode instead of the flashed app.</summary>
    public bool DownloadMode { get; init; }
}

/// <summary>
/// Runs an ESP32-C3 firmware in BoardSim's QEMU fork. The pin bridge and the
/// UART both connect back to sockets this class listens on, so nothing is
/// left behind when it stops.
/// </summary>
public sealed class QemuSession : IDisposable
{
    private readonly QemuOptions _options;
    private Process? _process;
    private TcpListener? _bridgeListener, _uartListener;
    private TcpClient? _bridge, _uart;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _log = new();

    public event Action<BridgeMessage>? Message;
    /// <summary>Raw bytes from the chip's UART0 (console output, or esptool's replies).</summary>
    public event Action<byte[]>? Uart;
    public event Action<int>? Exited;

    public QemuSession(QemuOptions options) => _options = options;

    public bool IsRunning => _process is { HasExited: false };
    public string Log { get { lock (_log) return _log.ToString(); } }
    public IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();
    /// <summary>The host UDP port actually forwarded to the guest's 2412 (another BoardSim may hold the default).</summary>
    public int? StreamPort { get; private set; }

    private static int FreeUdpPort(int wanted)
    {
        for (var port = wanted; port < wanted + 20; port++)
        {
            try
            {
                using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
                return port;
            }
            catch (SocketException) { }
        }
        using var any = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)any.Client.LocalEndPoint!).Port;
    }

    public void Start()
    {
        _bridgeListener = new TcpListener(IPAddress.Loopback, 0);
        _uartListener = new TcpListener(IPAddress.Loopback, 0);
        _bridgeListener.Start();
        _uartListener.Start();
        var bridgePort = ((IPEndPoint)_bridgeListener.LocalEndpoint).Port;
        var uartPort = ((IPEndPoint)_uartListener.LocalEndpoint).Port;

        StreamPort = _options.StreamPort is { } wanted ? FreeUdpPort(wanted) : null;
        var nic = "user,model=open_eth" + (StreamPort is { } sp ? $",hostfwd=udp:127.0.0.1:{sp}-:2412" : "");
        var args = new List<string>
        {
            "-nographic", "-machine", "esp32c3", "-icount", "3",
            "-drive", $"file={_options.FlashImage},if=mtd,format=raw",
            "-nic", nic,
            "-serial", $"tcp:127.0.0.1:{uartPort}",
            "-monitor", "none",
            "-chardev", $"socket,id=boardsim,host=127.0.0.1,port={bridgePort}",
        };
        if (_options.RomDir is { } rom) { args.Insert(0, rom); args.Insert(0, "-L"); }
        if (_options.DownloadMode) { args.Add("-global"); args.Add("driver=esp32c3.gpio,property=strap_mode,value=0x02"); }
        Arguments = args;

        var psi = new ProcessStartInfo(_options.QemuPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_log) _log.AppendLine(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_log) _log.AppendLine(e.Data); };
        _process.Exited += (_, _) => Exited?.Invoke(_process.ExitCode);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _ = Task.Run(() => RunBridge(_cts.Token));
        _ = Task.Run(() => RunUart(_cts.Token));
    }

    private async Task RunBridge(CancellationToken ct)
    {
        try
        {
            _bridge = await _bridgeListener!.AcceptTcpClientAsync(ct);
            using var reader = new StreamReader(_bridge.GetStream(), Encoding.ASCII, false, 1 << 16);
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
                if (BridgeMessage.Parse(line) is { } m) Message?.Invoke(m);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    private async Task RunUart(CancellationToken ct)
    {
        try
        {
            _uart = await _uartListener!.AcceptTcpClientAsync(ct);
            var stream = _uart.GetStream();
            var buffer = new byte[4096];
            int n;
            while (!ct.IsCancellationRequested && (n = await stream.ReadAsync(buffer, ct)) > 0)
                Uart?.Invoke(buffer[..n]);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        catch (SocketException) { }
    }

    /// <summary>Levels of the chip's input pins, bit n = GPIO n.</summary>
    public void SetInputs(uint mask) => Send($"I {mask:x}\n");

    /// <summary>Types into the chip's UART console.</summary>
    public void WriteConsole(string text) => WriteUart(Encoding.UTF8.GetBytes(text));

    /// <summary>Sends bytes to the chip's UART0.</summary>
    public void WriteUart(byte[] data)
    {
        if (_uart?.Connected != true) return;
        try { _uart.GetStream().Write(data); } catch (IOException) { }
    }

    /// <summary>Holds the chip in reset (CHIP_EN low) or releases it, booting in the given mode.</summary>
    public void Hold() => Send("H 1\n");

    public void Boot(bool downloadMode)
    {
        Send($"S {(downloadMode ? 0x02 : 0x08):x}\n");
        Send("H 0\n");
    }

    private void Send(string line)
    {
        if (_bridge?.Connected != true) return;
        try { _bridge.GetStream().Write(Encoding.ASCII.GetBytes(line)); } catch (IOException) { }
    }

    public void Stop()
    {
        _cts.Cancel();
        try { if (_process is { HasExited: false }) _process.Kill(); } catch (InvalidOperationException) { }
        _bridge?.Dispose();
        _uart?.Dispose();
        _bridgeListener?.Stop();
        _uartListener?.Stop();
    }

    public void Dispose() => Stop();
}
