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
- `src/BoardSim.App` - Avalonia 11.2.3 (pinned; the template wants 12, which
  has API changes). `Controls/BoardView` draws, `Rendering/BoardScene` builds
  the geometry once per board.
- `src/BoardSim.Cli` - `check`, `parts`, `pins`.
- `tests/` - xunit, fixtures are Julelys v4 and v3.1 plus KiCad's IPC-D-356
  exports of them.

## Screenshots without a display session

```bash
dotnet src/BoardSim.App/bin/Debug/net8.0/BoardSim.dll board.kicad_pcb --screenshot out.png [--select N] [--side back] [--layers] [--net NAME] [--simulate] [--drive U1.GPIO7=H]
```

The window opens, renders, saves and closes.

## Adding a rule

Implement `IRule`, add it to `Checker.AllRules()`, and add a test that fails
on a board with the fault and passes on Julelys v4. Avoid rules that fire on
v4: it is a reviewed board, so a hit there is usually a false positive.

## Next milestones

1. Done: DC logic simulation (mux, buffers, LEDs, switches, user-driven pins).
   Still open: WS2812 strings on the connectors, regulators turning rails on.
2. ESP32-C3 in Espressif's QEMU, with its pins bridged into the simulation,
   and a virtual serial port so `idf.py flash` works against it. QEMU lacks
   RMT, GPIO matrix, Wi-Fi and USB Serial/JTAG for the C3; those need a fork.
3. Physical board check: test firmware reads pins back over USB and BoardSim
   compares them with the netlist.
