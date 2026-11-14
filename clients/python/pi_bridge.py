#!/usr/bin/env python3
"""
Connects a real Raspberry Pi's pins to a board simulated by BoardSim, as if
the Pi were plugged into it.

  --in 17     the Pi reads its BCM 17 (pull-up; a button to GND) and drives
              the simulated board's GPIO17 (J1 pin 11 on a Pi header) to match
  --out 27    the simulated board's GPIO27 lights an LED on the Pi's BCM 27
  --map 17=J5.2   use another simulated pin than GPIO<n>

    python3 pi_bridge.py 192.168.1.20 --in 17 --out 27

Needs gpiozero (installed on Raspberry Pi OS) and boardsim_gpio.py next to it.
In BoardSim: Console tab -> "Allow from network".
"""
import argparse
import signal

from gpiozero import DigitalInputDevice, DigitalOutputDevice

from boardsim_gpio import Board


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("host", help="the machine running BoardSim")
    ap.add_argument("--port", type=int, default=4142)
    ap.add_argument("--in", dest="inputs", type=int, action="append", default=[], help="Pi BCM pin read and sent to the board")
    ap.add_argument("--out", dest="outputs", type=int, action="append", default=[], help="Pi BCM pin driven from the board")
    ap.add_argument("--map", action="append", default=[], help="BCM=PIN, e.g. 17=J5.2")
    args = ap.parse_args()

    mapping = {int(k): v for k, v in (m.split("=", 1) for m in args.map)}
    board = Board(args.host, args.port)
    print(board.banner)
    keep = []

    for bcm in args.inputs:
        target = mapping.get(bcm, f"GPIO{bcm}")
        dev = DigitalInputDevice(bcm, pull_up=True, bounce_time=0.01)
        # gpiozero: value is 1 when the pin is low with pull_up=True ("active"), so invert to the electrical level.
        dev.when_activated = lambda t=target: board.set(t, "low")
        dev.when_deactivated = lambda t=target: board.set(t, "high")
        board.set(target, "low" if dev.value else "high")
        keep.append(dev)
        print(f"Pi BCM {bcm} -> board {target}")

    for bcm in args.outputs:
        source = mapping.get(bcm, f"GPIO{bcm}")
        led = DigitalOutputDevice(bcm)
        level, _ = board.get(source)
        led.value = level == "high"
        board.watch(source, lambda pin, lvl, volts, l=led: setattr(l, "value", lvl == "high"))
        keep.append(led)
        print(f"board {source} -> Pi BCM {bcm}")

    print("Bridging; Ctrl+C to stop.")
    signal.pause()


if __name__ == "__main__":
    main()
