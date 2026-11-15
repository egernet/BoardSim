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
  - `Emc/` - noise and emission estimates: `EmcStackup` (layer heights from the
    board's stackup), `PlaneMap` (zone fills), `RouteAnalyzer` (reference plane per
    0.5 mm of track, loop area, reference changes), `EmcAnalysis` (switching nets
    from boardsim.json `signals`, USB, crystals, UART; followed through resistors,
    buffers and muxes; trapezoid spectrum, DM/CM emission, crosstalk) and the
    `emc-*` rules. Validated against julelys_pcb docs/noise-analysis-*.md.
  - `Components/ComponentData` - per part: the part model and the 3D model file
    (resolved like KiCad: KIPRJMOD, KICADn_3DMODEL_DIR, 3RD_PARTY, then by file
    name in the project folder and KiCad's library), with its height from the STEP.
    `Board3D` builds the 3D view: points every footprint at its resolved model,
    runs `kicad-cli pcb export glb` (STEP -> triangles, cached in ~/.boardsim/3d
    per board version) and reads the GLB (`Glb`, Z-up mm). Without KiCad: boxes
    at the model heights. The app draws it with OpenGL (`Controls/Board3DView`);
    `--screenshot` cannot capture OpenGL, use `--3d` and capture the window.
    `BoardRender` draws the 2D realistic view the way KiCad does: `kicad-cli pcb
    render` (orthographic, transparent, editor preset, uncoloured mask made green)
    of each side with and without models, cached in ~/.boardsim/3d. The bare
    render's alpha gives the board outline, which places both images on the
    Edge.Cuts box (parts may overhang, so the render with parts cannot). Bottom
    renders are mirrored. BoardView draws the image instead of its own copper,
    silk and bodies; pads, highlight, probes and simulation stay on top.
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

## Packaging and releases

`scripts/package.sh <platform> <version> <qemu dir>` packages one platform
(windows-x64, linux-x64, linux-arm64, macos-arm64) with an unpacked
qemu-boardsim archive. `.github/workflows/release.yml` does it for all of them
on a `v*` tag (draft pre-release) with the emulator from qemu-boardsim's
`QEMU_TAG` release; qemu-boardsim's own `boardsim.yml` builds those archives
(Windows static via MSYS2 UCRT64, Linux with bundled .so files and $ORIGIN,
macOS with bundled dylibs). The CLI is shipped as `boardsim-cli`: "boardsim"
would clash with "BoardSim" on case-insensitive file systems.
Test Linux locally: unpack the linux-arm64 tarball and run it in an
`ubuntu:22.04` container (GUI under `xvfb-run`).

## Packaging (macOS, by hand)

`scripts/package-macos.sh <version>` builds `dist/BoardSim.app` (self-contained
.NET, QEMU from `../qemu-esp` bundled with its dylibs by
`scripts/bundle-qemu-macos.sh`, the `examples/` folder) and a zip for a GitHub
release. The app is ad-hoc signed, not notarised. `examples/julelys-v4` holds
the board and a prebuilt Julelys firmware (the ordinary build plus boardsim_wifi).

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
   against the emulated chip. QEMU ships for Windows, macOS and Linux (v0.5.0).
3. Done (first version): noise and emission estimates (`Emc/`, EMC tab,
   `boardsim-cli emc`). Still open: field simulation of chosen nets (openEMS
   export with the 3D models), measured signals from the running firmware.

## Gotchas (EMC)

- Use the real parts: edge data (`riseNs`, `driveOhms`, `edgeSource`) belongs in
  the part model with its datasheet source, project models in `boardsim-parts/`.
  Generic values are a fallback and must show as generic.
- EMC findings are Info unless clearly bad: Julelys v4 must stay free of warnings.
- Crosstalk on a small board is tiny (saturation length is v x tr / 2, about
  22 cm at 3 ns); that is correct, not a bug.

## Gotchas (emulation)

- pyserial's RFC 2217 client waits ~50 ms for every DTR/RTS change, so the
  auto-reset boots download mode as soon as GPIO9 is seen low after EN is
  released, rather than sampling at a fixed delay.
- A blank flash makes the ROM print "invalid header" as fast as QEMU runs;
  console output to the port is throttled to 230400 baud or pyserial drowns.
- The flasher stub reads 0x3FCE0000+, which the fork backs with RAM.
3. Physical board check: test firmware reads pins back over USB and BoardSim
   compares them with the netlist.
