# BoardSim

.NET 8 app that loads KiCad boards, draws them and checks them. Meant to be
released for Windows, macOS and Linux, so UI text and messages are English.

## Layout

- `src/BoardSim.Core` - no UI dependencies.
  - `KiCad/` - s-expression parser and `.kicad_pcb` loader (KiCad 6-10). KiCad
    6/7 refer to nets by number on tracks; 8+ by name.
  - `Model/` - board, footprints, pads (absolute positions), tracks, zones.
    `Vec2.Rotate` matches KiCad (y down, positive = counter-clockwise on screen).
  - `Analysis/Connectivity` - copper islands per net (unrouted check).
  - `Analysis/Circuit` - parts, pins, nets, net voltages (name, regulator
    outputs, then through inductors and 0 ohm links).
  - `Analysis/Rules.cs`, `Expectations.cs`, `Checker.cs` - the rules.
  - `Parts/Models/*.json` - embedded part models. A model's `behavior`
    (`analogMux`, `buffer`, `mcu`) makes the part act in the simulation.
  - `Simulation/` - DC simulation: nodal solve, diodes on/off iteration,
    digital parts re-evaluated until the circuit settles.
  - `Emulation/` - runs ESP32-C3 firmware in BoardSim's QEMU fork
    (`../qemu-esp`, branch `boardsim`): `QemuSession` (process, pin bridge,
    UART), `EmulatedBoard` (pins into the simulation, inputs back, LED data
    traced to the connector it reaches), `Ws2812` decoding, `Firmware`
    (flash image from an ESP-IDF build folder, finding QEMU),
    `EmulatedChip` (persistent flash, power, the USB port), `Rfc2217Server`
    and `AutoReset` (DTR/RTS to EN/GPIO9 via the bridge's S/H/X commands),
    `GpioServer` (pins over TCP 4142, line protocol; every command ends with
    ok/err, watched pins push `change ...`). CHIP_EN/GPIO9 nets in the
    simulation reset the chip (`EmulatedBoard.EnableChanged`).
- `firmware/components/boardsim_wifi` - ESP-IDF component that wraps the
  esp_wifi API (`-Wl,--wrap`); in QEMU (SYSCON 0x600263F8 reads "QEMU") the
  station runs on openeth. Projects with MINIMAL_BUILD must REQUIRE it.
- `clients/python` - `boardsim_gpio.py` (no deps, RPi.GPIO style; callbacks
  run on their own thread so they may call get/set) and `pi_bridge.py`.
- `src/BoardSim.App` - Avalonia 11.2.3 (pinned; the template wants 12, which
  has API changes). `Controls/BoardView` draws, `Rendering/BoardScene` builds
  the geometry once per board.
- `src/BoardSim.Cli` - `check`, `parts`, `pins`.
- `tests/` - xunit, fixtures are Julelys v4 and v3.1 plus KiCad's IPC-D-356
  exports of them.

## Packaging (macOS)

`scripts/package-macos.sh <version>` builds `dist/BoardSim.app` (self-contained
.NET, QEMU from `../qemu-esp` bundled with its dylibs by
`scripts/bundle-qemu-macos.sh`, the `examples/` folder) and a zip for a GitHub
release. The app is ad-hoc signed, not notarised. `examples/julelys-v4` holds
the board and a prebuilt Julelys firmware (CONFIG_JULELYS_QEMU).

## Screenshots without a display session

```bash
dotnet src/BoardSim.App/bin/Debug/net8.0/BoardSim.dll board.kicad_pcb --screenshot out.png [--select N] [--side back] [--layers] [--net NAME] [--simulate] [--drive U1.GPIO7=H] [--firmware build-folder | --power] [--wait 14000] [--probe U1.GPIO7] [--scope led] [--crystal Y1=26]
```

The window opens, renders, saves and closes.

## Adding a rule

Implement `IRule`, add it to `Checker.AllRules()`, and add a test that fails
on a board with the fault and passes on Julelys v4. Avoid rules that fire on
v4: it is a reviewed board, so a hit there is usually a false positive.

## Next milestones

1. Done: DC logic simulation (mux, buffers, LEDs, switches, user-driven pins).
   Still open: WS2812 strings on the connectors, regulators turning rails on.
2. Done: ESP32-C3 firmware runs in the QEMU fork (GPIO, RMT, SAR ADC added),
   drives the simulation and shows the LED strings. Wi-Fi cannot be emulated:
   firmware uses the boardsim_wifi component (or openeth directly). `idf.py -p rfc2217://localhost:4141 flash` works
   against the emulated chip. Still open: shipping QEMU builds for Windows,
   macOS and Linux.

## Gotchas (emulation)

- pyserial's RFC 2217 client waits ~50 ms for every DTR/RTS change, so the
  auto-reset boots download mode as soon as GPIO9 is seen low after EN is
  released, rather than sampling at a fixed delay.
- A blank flash makes the ROM print "invalid header" as fast as QEMU runs;
  console output to the port is throttled to 230400 baud or pyserial drowns.
- The flasher stub reads 0x3FCE0000+, which the fork backs with RAM.
3. Physical board check: test firmware reads pins back over USB and BoardSim
   compares them with the netlist.
