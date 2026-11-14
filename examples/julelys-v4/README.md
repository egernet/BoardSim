# Julelys v4

The v4 board of [Julelys](https://github.com/egernet/julelys_pcb_v2), a
Christmas light controller: an ESP32-C3 drives four WS2812B strings
through a level shifter and an 8-channel analog multiplexer.

- `julelys_v4.kicad_pcb` - the board (KiCad 10).
- `julelys_v4.boardsim.json` - what the firmware expects the board to connect
  (GPIO7 to the mux through U6, GPIO4/5/8 to the mux select lines, ...), and
  that the strings take RGB byte order.
- `firmware/` - the Julelys firmware built for BoardSim
  ([julelys_esp32](https://github.com/egernet/julelys_esp32), branch
  `feature/qemu`, `CONFIG_JULELYS_QEMU=y`: emulated Ethernet instead of
  Wi-Fi). Without a stream it plays a rainbow on the strings after 5 seconds.

Open the board, then **Flash build…** and pick `firmware/` (the app's
"Try the Julelys example" does both).
