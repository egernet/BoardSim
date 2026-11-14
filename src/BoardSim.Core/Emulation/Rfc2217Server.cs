using System.Net;
using System.Net.Sockets;

namespace BoardSim.Core.Emulation;

/// <summary>
/// A serial port over TCP (RFC 2217, "telnet COM port control"). esptool,
/// idf.py and idf.py monitor open it as <c>rfc2217://localhost:PORT</c>, on
/// every OS and without a driver. Unlike a plain socket it carries DTR and RTS,
/// so the board's auto-reset circuit can be emulated.
/// </summary>
public sealed class Rfc2217Server : IDisposable
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    private const byte BINARY = 0, SGA = 3, COM_PORT = 44;
    private const byte SET_CONTROL = 5;
    private const byte DTR_ON = 8, DTR_OFF = 9, RTS_ON = 11, RTS_OFF = 12;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();
    private TcpClient? _client;

    public int Port { get; }
    public string Url => $"rfc2217://localhost:{Port}";
    public bool Connected => _client?.Connected == true;
    public bool Dtr { get; private set; }
    public bool Rts { get; private set; }

    /// <summary>Bytes the host wrote to the port.</summary>
    public event Action<byte[]>? Data;
    /// <summary>DTR or RTS changed (asserted = true), or the host disconnected (both false).</summary>
    public event Action<bool, bool>? ControlLines;
    public event Action<bool>? ConnectionChanged;

    public Rfc2217Server(int port = 4141)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        try { _listener.Start(); }
        catch (SocketException)
        {
            // Port taken (another BoardSim?): take any free one.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            // One host at a time, like a real port.
            _client?.Dispose();
            _client = client;
            client.NoDelay = true;
            ConnectionChanged?.Invoke(true);
            await Serve(client);
            if (_client == client)
            {
                _client = null;
                Dtr = Rts = false;
                ControlLines?.Invoke(false, false);
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    private async Task Serve(TcpClient client)
    {
        var stream = client.GetStream();
        var buf = new byte[8192];
        var data = new List<byte>(8192);
        var state = 0;             // 0 data, 1 after IAC, 2 option byte, 3 in SB, 4 IAC inside SB
        byte verb = 0;
        var sb = new List<byte>();
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf, _cts.Token)) > 0)
            {
                data.Clear();
                for (var i = 0; i < n; i++)
                {
                    var b = buf[i];
                    switch (state)
                    {
                        case 0:
                            if (b == IAC) state = 1; else data.Add(b);
                            break;
                        case 1:
                            if (b == IAC) { data.Add(IAC); state = 0; }
                            else if (b is DO or DONT or WILL or WONT) { verb = b; state = 2; }
                            else if (b == SB) { sb.Clear(); state = 3; }
                            else state = 0;
                            break;
                        case 2:
                            Negotiate(verb, b);
                            state = 0;
                            break;
                        case 3:
                            if (b == IAC) state = 4; else sb.Add(b);
                            break;
                        case 4:
                            if (b == SE) { Subnegotiation(sb); state = 0; }
                            else { sb.Add(b); state = 3; }   // IAC IAC inside SB
                            break;
                    }
                }
                if (data.Count > 0) Data?.Invoke(data.ToArray());
            }
        }
        catch (Exception) { }
    }

    private void Negotiate(byte verb, byte option)
    {
        var supported = option is BINARY or SGA or COM_PORT;
        byte reply = verb switch
        {
            WILL => supported ? DO : DONT,
            DO => supported ? WILL : WONT,
            WONT => DONT,
            _ => WONT,
        };
        SendRaw(new[] { IAC, reply, option });
    }

    private void Subnegotiation(List<byte> sb)
    {
        if (sb.Count < 2 || sb[0] != COM_PORT) return;
        var command = sb[1];
        var value = sb.Skip(2).ToArray();
        if (command == SET_CONTROL && value.Length == 1)
        {
            var (dtr, rts) = (Dtr, Rts);
            switch (value[0])
            {
                case DTR_ON: dtr = true; break;
                case DTR_OFF: dtr = false; break;
                case RTS_ON: rts = true; break;
                case RTS_OFF: rts = false; break;
            }
            if (dtr != Dtr || rts != Rts)
            {
                Dtr = dtr;
                Rts = rts;
                ControlLines?.Invoke(dtr, rts);
            }
        }
        // Every setting is acknowledged by echoing it back with the server code (+100).
        var reply = new List<byte> { IAC, SB, COM_PORT, (byte)(command + 100) };
        foreach (var v in value) { reply.Add(v); if (v == IAC) reply.Add(IAC); }
        reply.Add(IAC);
        reply.Add(SE);
        SendRaw(reply.ToArray());
    }

    /// <summary>Sends bytes from the chip to the host.</summary>
    public void Write(byte[] data)
    {
        if (_client is null) return;
        var escaped = new List<byte>(data.Length + 8);
        foreach (var b in data) { escaped.Add(b); if (b == IAC) escaped.Add(IAC); }
        SendRaw(escaped.ToArray());
    }

    private void SendRaw(byte[] bytes)
    {
        var c = _client;
        if (c is null) return;
        lock (_sendLock)
        {
            try { c.GetStream().Write(bytes); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
        _listener.Stop();
    }
}

/// <summary>
/// The classic two-transistor auto-reset circuit: RTS asserted alone pulls
/// EN low (reset), DTR asserted alone pulls GPIO9 low (download mode).
/// On a real board the RC on EN keeps the chip in reset for a moment while
/// esptool flips both lines, so GPIO9 is low when the chip samples it. Over
/// RFC 2217 each line change takes a network round trip, so instead: after
/// EN is released, boot in download mode as soon as GPIO9 is seen low, or
/// from flash when it has not been low for <see cref="FlashBootAfterMs"/>.
/// </summary>
public sealed class AutoReset
{
    private readonly Action<bool> _hold;
    private readonly Action<bool> _boot;
    private readonly object _lock = new();
    private bool _held;
    private CancellationTokenSource? _pending;

    /// <param name="hold">Hold the chip in reset.</param>
    /// <param name="boot">Release it; true = download mode.</param>
    public AutoReset(Action<bool> hold, Action<bool> boot)
    {
        _hold = hold;
        _boot = boot;
    }

    public int FlashBootAfterMs { get; init; } = 300;

    public void Update(bool dtr, bool rts)
    {
        lock (_lock)
        {
            var enLow = rts && !dtr;
            var io9Low = dtr && !rts;
            if (enLow)
            {
                _pending?.Cancel();
                _pending = null;
                if (!_held) { _held = true; _hold(true); }
                return;
            }
            if (!_held) return;
            if (io9Low)
            {
                _pending?.Cancel();
                _pending = null;
                _held = false;
                _boot(true);
                return;
            }
            if (_pending is not null) return;
            var cts = _pending = new CancellationTokenSource();
            _ = Task.Delay(FlashBootAfterMs, cts.Token).ContinueWith(t =>
            {
                lock (_lock)
                {
                    if (t.IsCanceled || _pending != cts) return;
                    _pending = null;
                    _held = false;
                    _boot(false);
                }
            }, TaskScheduler.Default);
        }
    }
}
