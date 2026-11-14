using System.Security.Cryptography;
using System.Text;
using BoardSim.Core.Analysis;
using BoardSim.Core.Simulation;

namespace BoardSim.Core.Emulation;

/// <summary>
/// The board's ESP32-C3 as a device you can plug in: it has its own flash
/// (kept between runs, like a real board), a virtual USB serial port that
/// esptool and idf.py use, and the auto-reset circuit behind it.
/// </summary>
public sealed class EmulatedChip : IDisposable
{
    private readonly (string Qemu, string? Rom) _qemu;
    private QemuSession? _session;
    private readonly AutoReset _autoReset;
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public EmulatedBoard Board { get; }
    public Rfc2217Server Port { get; }
    public string FlashPath { get; }
    public bool IsRunning => _session?.IsRunning == true;
    /// <summary>True while the chip sits in the ROM's download mode (esptool is talking to it).</summary>
    public bool DownloadMode { get; private set; }
    public bool HeldInReset { get; private set; }
    public int? StreamPort { get; init; } = 12412;
    /// <summary>The UDP port the running chip's stream is forwarded from.</summary>
    public int? ActiveStreamPort => _session?.StreamPort;
    /// <summary>Report DTR/RTS changes on Status (for debugging resets).</summary>
    public bool TraceControlLines { get; set; }

    /// <summary>UART text for the console view (not raw esptool traffic).</summary>
    public event Action<string>? Console;
    /// <summary>Something worth a status line: reset, download mode, esptool connected.</summary>
    public event Action<string>? Status;
    public event Action<int>? Exited;

    public EmulatedChip(EmulatedBoard board, (string Qemu, string? Rom) qemu, string flashPath, int port = 4141)
    {
        Board = board;
        _qemu = qemu;
        FlashPath = flashPath;
        Port = new Rfc2217Server(port);
        _autoReset = new AutoReset(
            hold: _ =>
            {
                HeldInReset = true;
                _session?.Hold();
            },
            boot: download =>
            {
                HeldInReset = false;
                DownloadMode = download;
                _session?.Boot(download);
                Status?.Invoke(download ? "Reset into download mode (GPIO9 low)" : "Reset: booting from flash");
            });
        Port.Data += data => _session?.WriteUart(BaudMismatch ? Garble(data, 1 / Board.Simulator.ClockRatio(Board.Mcu)) : data);
        Port.ControlLines += (dtr, rts) =>
        {
            if (TraceControlLines) Status?.Invoke($"DTR={(dtr ? 1 : 0)} RTS={(rts ? 1 : 0)}");
            _autoReset.Update(dtr, rts);
        };
        Port.ConnectionChanged += connected =>
            Status?.Invoke(connected ? $"Host connected to {Port.Url}" : $"Host disconnected from {Port.Url}");
    }

    /// <summary>~/.boardsim/flash/&lt;board&gt;-&lt;hash&gt;.bin: one flash per board file.</summary>
    public static string DefaultFlashPath(string boardPath)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(boardPath))))[..8].ToLowerInvariant();
        var name = new string(Path.GetFileNameWithoutExtension(boardPath).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Path.Combine(home, ".boardsim", "flash", $"{name}-{hash}.bin");
    }

    public bool FlashIsBlank()
    {
        if (!File.Exists(FlashPath)) return true;
        using var f = File.OpenRead(FlashPath);
        var head = new byte[32];
        f.ReadExactly(head);
        return head.All(b => b == 0xff);
    }

    public void PowerOn()
    {
        if (IsRunning) return;
        if (!File.Exists(FlashPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlashPath)!);
            Firmware.WriteBlank(FlashPath);
        }
        DownloadMode = false;
        HeldInReset = false;
        _session = new QemuSession(new QemuOptions
        {
            QemuPath = _qemu.Qemu, RomDir = _qemu.Rom, FlashImage = FlashPath, StreamPort = StreamPort,
        });
        Board.SendInputs = mask => _session?.SetInputs(mask);
        _session.Message += Board.Handle;
        _session.Uart += OnUart;
        var session = _session;
        _session.Exited += code => { if (_session == session) Exited?.Invoke(code); };
        _session.Start();
    }

    // A real UART cannot send faster than its baud rate. Emulated code can,
    // and a boot loop on blank flash would flood the host (pyserial's RFC 2217
    // reader handles one byte at a time in Python), so console output is held
    // to 230400 baud; anything beyond that is dropped, like a full FIFO.
    private const double ConsoleBytesPerSecond = 230400 / 10.0;
    private double _tokens = ConsoleBytesPerSecond / 4;
    private long _lastTicks = Environment.TickCount64;

    /// <summary>
    /// With the wrong crystal the UART runs at the wrong baud rate. Beyond about
    /// 4 % the receiver samples the wrong bits: the host sees garbage, and what
    /// the host sends arrives as garbage too.
    /// </summary>
    public bool BaudMismatch => Math.Abs(Board.Simulator.ClockRatio(Board.Mcu) - 1) > 0.04;

    private static byte[] Garble(byte[] data, double ratio)
    {
        var shift = (int)Math.Round(Math.Log2(ratio) * 8);
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            result[i] = (byte)(shift >= 0 ? (b << shift % 8) | (b >> (8 - shift % 8)) ^ 0x5a : (b >> -shift % 8) ^ 0xa5);
        }
        return result;
    }

    private void OnUart(byte[] data)
    {
        if (BaudMismatch) data = Garble(data, Board.Simulator.ClockRatio(Board.Mcu));
        if (DownloadMode)
        {
            Port.Write(data);       // esptool's SLIP traffic: pass it all, it is request/response
            return;
        }
        var now = Environment.TickCount64;
        _tokens = Math.Min(ConsoleBytesPerSecond / 4, _tokens + (now - _lastTicks) / 1000.0 * ConsoleBytesPerSecond);
        _lastTicks = now;
        var n = (int)Math.Min(data.Length, _tokens);
        if (n <= 0) return;
        _tokens -= n;
        if (n < data.Length) data = data[..n];
        Port.Write(data);
        var chars = new char[_utf8.GetCharCount(data, 0, data.Length)];
        _utf8.GetChars(data, 0, data.Length, chars, 0);
        Console?.Invoke(new string(chars));
    }

    public void PowerOff()
    {
        var s = _session;
        _session = null;
        s?.Dispose();
    }

    /// <summary>Presses reset (like the EN button): reboots from flash.</summary>
    public void Reset()
    {
        DownloadMode = false;
        _session?.Boot(false);
    }

    /// <summary>Writes an ESP-IDF build (or merged image) into the chip's flash, then boots it.</summary>
    public void LoadBuild(string path)
    {
        var image = Firmware.BuildImage(path);
        var wasRunning = IsRunning;
        PowerOff();
        Directory.CreateDirectory(Path.GetDirectoryName(FlashPath)!);
        File.WriteAllBytes(FlashPath, image);
        PowerOn();
        if (!wasRunning) Status?.Invoke($"Flashed {Path.GetFileName(path.TrimEnd('/', '\\'))} and powered on");
    }

    public void WriteConsole(string text) => _session?.WriteConsole(text);

    public string Log => _session?.Log ?? "";

    public void Dispose()
    {
        PowerOff();
        Port.Dispose();
    }
}
