"""
Talk to a board simulated by BoardSim, as if your program ran on a Raspberry
Pi plugged into it. No dependencies; Python 3.7+.

Object style:

    from boardsim_gpio import Board
    board = Board("192.168.1.20")            # the Mac running BoardSim (port 4142)
    board.set("GPIO17", True)                # Pi BCM 17 = J1 pin 11 on a Pi header
    print(board.get("J1.24"))                # ('high', 3.3)
    board.watch("J1.24", lambda pin, level, volts: print(pin, level, volts))

RPi.GPIO style (BCM or BOARD numbering, against the board's Pi header):

    import boardsim_gpio as GPIO             # instead of: import RPi.GPIO as GPIO
    GPIO.connect("192.168.1.20")             # or set BOARDSIM_HOST
    GPIO.setmode(GPIO.BCM)
    GPIO.setup(17, GPIO.OUT)
    GPIO.output(17, GPIO.HIGH)
    GPIO.setup(8, GPIO.IN)
    GPIO.add_event_detect(8, GPIO.BOTH, callback=lambda ch: print(ch, GPIO.input(ch)))

In BoardSim, turn on "Allow from network" in the Console tab for a Pi on the
LAN to reach it (or run: boardsim chip board.kicad_pcb --lan).
"""
import os
import queue
import socket
import threading

__all__ = ["Board", "BoardSimError"]


class BoardSimError(Exception):
    pass


class Board:
    def __init__(self, host=None, port=4142, timeout=5.0):
        host = host or os.environ.get("BOARDSIM_HOST", "localhost")
        self._sock = socket.create_connection((host, port), timeout=timeout)
        self._sock.settimeout(None)
        self._file = self._sock.makefile("r", encoding="ascii", newline="\n")
        self._answers = queue.Queue()
        self._callbacks = {}
        self._lock = threading.Lock()
        self._events = queue.Queue()
        self.banner = self._file.readline().strip()
        threading.Thread(target=self._reader, daemon=True).start()
        # Callbacks run on their own thread, so they may call get()/set() themselves.
        threading.Thread(target=self._dispatch, daemon=True).start()

    def _reader(self):
        for raw in self._file:
            line = raw.strip()
            if line.startswith("change "):
                self._events.put(line)
            else:
                self._answers.put(line)
        self._answers.put("err connection closed")

    def _dispatch(self):
        while True:
            _, pin, level, *rest = self._events.get().split()
            volts = float(rest[0]) if rest else None
            for cb in list(self._callbacks.get(pin, [])):
                try:
                    cb(pin, level, volts)
                except Exception as e:      # one bad callback must not stop the others
                    print("boardsim_gpio callback error:", e)

    def command(self, line):
        """Sends one command and returns its lines (the last one starts with ok)."""
        with self._lock:
            self._sock.sendall((line + "\n").encode("ascii"))
            lines = []
            while True:
                answer = self._answers.get(timeout=10)
                lines.append(answer)
                if answer.startswith("err"):
                    raise BoardSimError(answer[4:])
                if answer.startswith("ok"):
                    return lines

    def set(self, pin, value):
        """True/'high', False/'low', None/'input' (release), or a voltage like 3.3."""
        if value is True:
            value = "high"
        elif value is False:
            value = "low"
        elif value is None:
            value = "input"
        self.command(f"set {pin} {value}")

    def pulse(self, pin, value, ms):
        self.command(f"pulse {pin} {'high' if value in (True, 1, 'high') else 'low'} {int(ms)}")

    def get(self, pin):
        """Returns (level, volts): level is high, low, between, floating or conflict."""
        line = self.command(f"get {pin}")[0].split()
        return line[1], (float(line[2]) if len(line) > 2 else None)

    def watch(self, pin, callback):
        """callback(pin_id, level, volts) on every change. Returns the pin's id (e.g. J1.24)."""
        lines = self.command(f"watch {pin}")
        ids = [l.split()[0] for l in lines[:-1]]
        for pid in ids:
            self._callbacks.setdefault(pid, []).append(callback)
        return ids[0] if len(ids) == 1 else ids

    def pins(self, ref=""):
        """[id, name, net, kind] for every pin that can be driven or watched."""
        return [l.split() for l in self.command(f"pins {ref}".strip())[:-1]]

    def close(self):
        self._sock.close()


# --- RPi.GPIO style --------------------------------------------------------

BCM, BOARD = 11, 10
OUT, IN = 0, 1
LOW, HIGH = 0, 1
PUD_OFF, PUD_DOWN, PUD_UP = 20, 21, 22
RISING, FALLING, BOTH = 31, 32, 33

_board = None
_mode = None
_header = None
_last = {}


def connect(host=None, port=4142):
    global _board
    _board = Board(host, port)
    return _board


def _b():
    return _board or connect()


def setwarnings(flag):
    pass


def setmode(mode):
    global _mode
    _mode = mode


def _pin(channel):
    global _header
    if _mode == BOARD:
        if _header is None:
            # The connector that has the Pi's GPIO names (J1 on a HAT).
            gpio = [p for p in _b().pins() if len(p) > 3 and p[3] == "connector" and p[1].startswith("GPIO")]
            if not gpio:
                raise BoardSimError("no Raspberry Pi header on this board; use BCM numbering")
            _header = gpio[0][0].split(".")[0]
        return f"{_header}.{channel}"
    return f"GPIO{channel}"


def setup(channel, direction, pull_up_down=PUD_OFF, initial=None):
    for ch in channel if isinstance(channel, (list, tuple)) else [channel]:
        if direction == OUT:
            _b().set(_pin(ch), bool(initial) if initial is not None else False)
        else:
            _b().set(_pin(ch), None)   # input: let the board decide the level


def output(channel, value):
    for ch in channel if isinstance(channel, (list, tuple)) else [channel]:
        _b().set(_pin(ch), bool(value))


def input(channel):
    return HIGH if _b().get(_pin(channel))[0] == "high" else LOW


def add_event_detect(channel, edge, callback=None, bouncetime=None):
    _last[channel] = input(channel)

    def changed(pin, level, volts):
        value = HIGH if level == "high" else LOW
        before, _last[channel] = _last.get(channel), value
        if value == before:
            return
        if edge == BOTH or (edge == RISING and value == HIGH) or (edge == FALLING and value == LOW):
            if callback:
                callback(channel)

    _b().watch(_pin(channel), changed)


def remove_event_detect(channel):
    _b().command(f"unwatch {_pin(channel)}")


def cleanup(channel=None):
    if _board:
        _board.command("unwatch")
