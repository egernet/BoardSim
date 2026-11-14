# BoardSim

Open a KiCad board and see what is wrong with it before it is made.

![BoardSim](assets/logo-1024.png)

BoardSim reads a `.kicad_pcb` file (KiCad 6 to 10), draws it the way the
finished board looks, and checks every connection:

- **Routing:** nets whose pads are not joined by copper.
- **Part models:** power pins on the wrong rail, 5 V on a 3.3 V pin, a 3.3 V
  output into a 5 V CMOS input, strapping pins that read the wrong level at
  boot, floating inputs, missing pulls, regulator and RC capacitance.
- **Design rules:** decoupling distance, tri-state lines that float on a
  connector, unprotected connector lines, labels that reach one pin.
- **Firmware expectations:** connections listed in `<board>.boardsim.json`
  next to the board, e.g. "GPIO7 reaches the mux input", traced through
  resistors and buffers.

Runs on Windows, macOS and Linux (.NET 8, Avalonia).

## Run

```bash
dotnet run --project src/BoardSim.App -- path/to/board.kicad_pcb
dotnet run --project src/BoardSim.Cli -- check path/to/board.kicad_pcb
```

In the app: drag to pan, scroll to zoom, `F` to fit, click a pad to light up
its net, click a finding to jump to it.

## Run firmware

The board's ESP32-C3 runs real firmware. Its GPIO outputs drive the
simulation, buttons and pulls reach its inputs, and LED data from the RMT is
decoded and shown on the connector it reaches.

**Power on** boots the chip from its own flash, which is kept between runs
(`~/.boardsim/flash/`), like a real board. The chip has a virtual USB
serial port, so you flash and monitor it with the usual tools:

```bash
idf.py -p rfc2217://localhost:4141 flash monitor
```

The port is RFC 2217 (serial over TCP), which works on Windows, macOS and
Linux without a driver. It carries DTR and RTS, and BoardSim emulates the
board's auto-reset circuit, so esptool resets the chip into download mode
and back exactly as on hardware. **Flash build…** writes an ESP-IDF build
folder straight into the flash instead.

Headless: `boardsim chip board.kicad_pcb` powers the chip and serves the
same port.

This needs BoardSim's QEMU fork,
[egernet/qemu-boardsim](https://github.com/egernet/qemu-boardsim) (branch
`boardsim`), which adds GPIO, RMT and SAR ADC emulation, a pin bridge and
reset control to Espressif's QEMU. Point `BOARDSIM_QEMU` at its
`qemu-system-riscv32`.

QEMU cannot emulate Wi-Fi. Firmware that needs a network should use the
emulated OpenCores Ethernet (`CONFIG_ETH_USE_OPENETH`); UDP 2412 in the
guest is forwarded from `127.0.0.1:12412` (or the next free port).

## Expected connections

```jsonc
// board.boardsim.json
{
  "connections": [
    { "from": "U1.GPIO7", "to": "U2.Z", "why": "LED data (RMT)" },
    { "from": "U2.Y0", "to": "J4.2" }
  ]
}
```

Pins are `REF.PIN`, by number or by the name in the part model.

## Part models

A part model is a JSON file describing the pins of one part: type, voltage
range, absolute maximum, input threshold, strapping level, required pulls and
capacitance. Built-in models live in `src/BoardSim.Core/Parts/Models/`. Put
your own `*.part.json` in a `boardsim-parts` folder next to the board.
Parts without a model still get the generic checks.

## Tests

```bash
dotnet test
```

The tests load Julelys v4 (KiCad 10) and v3.1 (KiCad 7) and compare pad
positions and nets with KiCad's own IPC-D-356 export. On v3.1 the rules must
find the faults that v4 fixed; v4 must be clean.

## License

MIT. The test boards in `tests/fixtures` are Julelys v4 and v3.1 from
[egernet/julelys_pcb_v2](https://github.com/egernet/julelys_pcb_v2).
