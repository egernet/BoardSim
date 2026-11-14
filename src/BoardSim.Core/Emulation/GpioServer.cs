using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using BoardSim.Core.Analysis;
using BoardSim.Core.Simulation;

namespace BoardSim.Core.Emulation;

/// <summary>
/// The board's pins over TCP, for anything outside BoardSim: a Raspberry Pi,
/// a script, a test. One command per line, one answer per line:
/// <code>
/// pins [J1]              list pins you can drive and watch
/// get J1.11              -> J1.11 high 3.30, then ok
/// set GPIO17 high        drive a pin: high, low, input (release) or volts (3.3)
/// pulse J1.29 low 100    drive for 100 ms, then release
/// watch J1 | J1.24 ...   report changes:  change J1.24 high 3.30
/// unwatch [pins]
/// </code>
/// Pins are REF.PIN by number or name (J1.11, J1.GPIO17), or a bare pin name
/// (GPIO17), looked up on the connectors first. Every command ends with a
/// line starting "ok" or "err".
/// </summary>
public sealed class GpioServer : IDisposable
{
    private readonly Simulator _sim;
    private readonly Action<Action> _mutate;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Client> _clients = new();
    private TcpListener _listener;

    public int Port { get; }
    public bool Lan { get; private set; }
    public int ClientCount { get { lock (_clients) return _clients.Count; } }

    /// <summary>A chip whose pins belong to running firmware: clients may watch them but not drive them.</summary>
    public Part? FirmwarePart { get; set; }

    /// <summary>Raised (on a server thread) when a client drives a pin.</summary>
    public event Action<string>? Activity;

    /// <param name="mutate">
    /// Runs a change to the simulation where it is safe (UI thread, or under the
    /// emulation lock) and solves the circuit again.
    /// </param>
    public GpioServer(Simulator sim, Action<Action> mutate, int port = 4142, bool lan = false)
    {
        _sim = sim;
        _mutate = mutate;
        (_listener, Port) = Listen(port, lan);
        Lan = lan;
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(WatchLoop);
    }

    private static (TcpListener, int) Listen(int port, bool lan)
    {
        var address = lan ? IPAddress.Any : IPAddress.Loopback;
        foreach (var p in new[] { port, 0 })
        {
            try
            {
                var l = new TcpListener(address, p);
                l.Start();
                return (l, ((IPEndPoint)l.LocalEndpoint).Port);
            }
            catch (SocketException) when (p != 0) { }
        }
        throw new InvalidOperationException("No free port for the GPIO server");
    }

    /// <summary>Listen on all interfaces (true) so a Pi on the LAN can connect, or only on this machine.</summary>
    public void SetLan(bool lan)
    {
        if (lan == Lan) return;
        _listener.Stop();
        (_listener, _) = Listen(Port, lan);
        Lan = lan;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Addresses a client can use: localhost, plus this machine's LAN addresses when allowed.</summary>
    public IReadOnlyList<string> Addresses()
    {
        var list = new List<string> { $"localhost:{Port}" };
        if (!Lan) return list;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var a in ni.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    list.Add($"{a.Address}:{Port}");
        return list;
    }

    private sealed class Client(TcpClient tcp)
    {
        public TcpClient Tcp { get; } = tcp;
        public readonly object WriteLock = new();
        public readonly Dictionary<PinRef, string> Watched = new();

        public void Send(string line)
        {
            lock (WriteLock)
            {
                try { Tcp.GetStream().Write(Encoding.ASCII.GetBytes(line + "\n")); }
                catch (Exception) { }
            }
        }
    }

    private async Task AcceptLoop()
    {
        var listener = _listener;
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            tcp.NoDelay = true;
            var client = new Client(tcp);
            lock (_clients) _clients.Add(client);
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(Client client)
    {
        client.Send($"boardsim gpio 1 - board {_sim.Circuit.Board.Title} - type 'help'");
        try
        {
            using var reader = new StreamReader(client.Tcp.GetStream(), Encoding.ASCII);
            while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                foreach (var answer in Execute(client, line.Trim())) client.Send(answer);
            }
        }
        catch (Exception) { }
        lock (_clients) _clients.Remove(client);
        client.Tcp.Dispose();
    }

    private IEnumerable<string> Execute(Client client, string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        switch (cmd)
        {
            case "help":
                yield return "commands: pins [REF] | get PIN | set PIN high|low|input|<volts> | pulse PIN high|low <ms> | watch PIN|REF ... | unwatch [PIN ...]";
                yield return "pins: J1.11, J1.GPIO17, or GPIO17 (looked up on the connectors)";
                yield return "every command ends with a line starting 'ok' or 'err'; watched pins report 'change PIN LEVEL VOLTS'";
                yield return "ok";
                yield break;
            case "pins":
            {
                var pins = Controllable().Where(p => parts.Length < 2 || string.Equals(p.Part.Reference, parts[1], StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var p in pins) yield return $"{p.Id} {p.Name} {p.Net.DisplayName} {p.Part.Kind.ToString().ToLowerInvariant()}";
                yield return $"ok {pins.Count} pins";
                yield break;
            }
            case "get" when parts.Length >= 2:
            {
                var unknown = parts.Skip(1).FirstOrDefault(p => Resolve(p) is null);
                if (unknown is not null) { yield return $"err unknown pin {unknown}"; yield break; }
                foreach (var p in parts.Skip(1)) yield return Describe(Resolve(p)!);
                yield return "ok";
                yield break;
            }
            case "set" when parts.Length >= 3:
            case "pulse" when parts.Length >= 4:
            {
                if (Resolve(parts[1]) is not { } pin) { yield return $"err unknown pin {parts[1]}"; yield break; }
                if (pin.Part == FirmwarePart) { yield return $"err {pin.Id} belongs to the running firmware; drive the pin on the other side (e.g. the connector)"; yield break; }
                if (!ParseDrive(parts[2], out var drive, out var volts)) { yield return $"err level must be high, low, input or volts, not {parts[2]}"; yield break; }
                _mutate(() => _sim.SetPin(pin, drive, volts));
                Activity?.Invoke($"{pin.Id} {parts[2]}");
                if (cmd == "pulse" && int.TryParse(parts[3], out var ms))
                {
                    _ = Task.Delay(Math.Clamp(ms, 1, 60000)).ContinueWith(_ => _mutate(() => _sim.SetPin(pin, PinDrive.Input)));
                    yield return $"ok {pin.Id} {parts[2]} for {ms} ms";
                }
                else yield return $"ok {Describe(pin)}";
                yield break;
            }
            case "watch" when parts.Length >= 2:
            {
                var added = new List<PinRef>();
                foreach (var spec in parts.Skip(1))
                {
                    var whole = _sim.Circuit.Part(spec);
                    var pins = whole is not null ? Controllable().Where(p => p.Part == whole).ToList()
                             : Resolve(spec) is { } one ? new List<PinRef> { one } : new List<PinRef>();
                    if (pins.Count == 0) { yield return $"err unknown pin {spec}"; continue; }
                    lock (client.WriteLock)
                        foreach (var p in pins) client.Watched[p] = State(p);
                    added.AddRange(pins);
                }
                foreach (var p in added) yield return Describe(p);
                yield return $"ok watching {added.Count} pins";
                yield break;
            }
            case "unwatch":
            {
                lock (client.WriteLock)
                {
                    if (parts.Length == 1) client.Watched.Clear();
                    foreach (var spec in parts.Skip(1))
                        if (Resolve(spec) is { } p) client.Watched.Remove(p);
                }
                yield return "ok";
                yield break;
            }
            default:
                yield return $"err unknown command '{line}' (try help)";
                yield break;
        }
    }

    private static bool ParseDrive(string s, out PinDrive drive, out double? volts)
    {
        volts = null;
        switch (s.ToLowerInvariant())
        {
            case "high" or "1" or "on": drive = PinDrive.High; return true;
            case "low" or "0" or "off": drive = PinDrive.Low; return true;
            case "input" or "in" or "release" or "z": drive = PinDrive.Input; return true;
        }
        if (double.TryParse(s.TrimEnd('v', 'V'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            drive = v > 0.05 ? PinDrive.High : PinDrive.Low;
            volts = v;
            return true;
        }
        drive = PinDrive.Input;
        return false;
    }

    /// <summary>Pins on connectors (and the chip's pins when no firmware owns them).</summary>
    private IEnumerable<PinRef> Controllable() =>
        _sim.ControllablePins.OrderBy(p => p.Part.Kind != PartKind.Connector).ThenBy(p => p.Part.Reference)
            .ThenBy(p => p.Number.Length).ThenBy(p => p.Number);

    public PinRef? Resolve(string spec)
    {
        if (ExpectationRule.Resolve(_sim.Circuit, spec) is { } pin) return pin;
        // A bare name: GPIO17 on the Pi header, CH1, ...
        return Controllable().FirstOrDefault(p => string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase))
            ?? _sim.Circuit.Parts.SelectMany(p => p.Pins).FirstOrDefault(p => string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase));
    }

    private string State(PinRef pin)
    {
        if (!_sim.Nets.TryGetValue(pin.Net, out var r)) return "unknown";
        var level = r.State switch
        {
            NetState.High => "high", NetState.Low => "low", NetState.Floating => "floating",
            NetState.Conflict => "conflict", _ => "between",
        };
        return r.Volts is { } v ? $"{level} {v.ToString("0.00", CultureInfo.InvariantCulture)}" : level;
    }

    private string Describe(PinRef pin) => $"{pin.Id} {State(pin)}";

    private async Task WatchLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(20, _cts.Token); } catch (OperationCanceledException) { return; }
            Client[] clients;
            lock (_clients) clients = _clients.ToArray();
            foreach (var c in clients)
            {
                List<string> changes = new();
                lock (c.WriteLock)
                {
                    foreach (var (pin, last) in c.Watched.ToList())
                    {
                        var now = State(pin);
                        if (now == last) continue;
                        // Ignore millivolt wobble; report level changes and real voltage steps.
                        if (Level(now) == Level(last) && Math.Abs(Volts(now) - Volts(last)) < 0.1) continue;
                        c.Watched[pin] = now;
                        changes.Add($"{pin.Id} {now}");
                    }
                }
                foreach (var line in changes) c.Send("change " + line);
            }
        }
    }

    private static string Level(string state) => state.Split(' ')[0];
    private static double Volts(string state) =>
        state.Split(' ') is [_, var v] && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Tcp.Dispose();
    }
}
