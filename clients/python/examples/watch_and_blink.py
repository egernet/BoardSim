#!/usr/bin/env python3
"""
Plays the Raspberry Pi on the Julelys v4 board: prints what the ESP32 does on
the SPI lines and holds the ESP32 in reset for a second with GPIO5 (CHIP_EN).

    python3 watch_and_blink.py [host]
"""
import sys
import time
sys.path.insert(0, __import__("os").path.dirname(__file__) + "/..")
from boardsim_gpio import Board

board = Board(sys.argv[1] if len(sys.argv) > 1 else None, int(sys.argv[2]) if len(sys.argv) > 2 else 4142)
print(board.banner)
board.watch("J1", lambda pin, level, volts: print(f"  {pin:6} {level:8} {volts} V"))
print("Holding the ESP32 in reset for a second (Pi GPIO5 -> CHIP_EN)...")
board.set("GPIO5", "low")
time.sleep(1)
board.set("GPIO5", "input")
print("Released. Watching J1 for 10 s.")
time.sleep(10)
