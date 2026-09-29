"""A minimal PLC for the Python tool's own tests: Modbus TCP (FC03/06/16) plus a 10 ms scan implementing
protocol.md map v1 just enough for the checklist. It is NOT the acceptance target: the C# test app's simulator is.

It has its own tiny Modbus server because pymodbus 3.15's datastore is mid-refactor and exposes no stable write hook.
Options mirror the C# simulator's configuration keys, so the integration fixture can start either one:
``Simulator__MapVersion``, ``Simulator__PublishLimits``, ``Simulator__Faults__<Name>`` (``WatchdogDisabled``,
``SuppressAck``, ``SwappedWordOrder``).

Run standalone like the test app: ``python tests/stub_plc.py --headless --port 5020``.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import math
import os
import struct
import sys
import time
from collections.abc import Callable
from dataclasses import dataclass, field, fields
from types import TracebackType

C, S = 0, 100  # block bases (protocol.md § Transport defaults)
SCAN_S = 0.01
WATCHDOG_S = 1.0
ERROR_STOP, DISABLED, STANDSTILL, HOMING, DISCRETE, CONTINUOUS, STOPPING = 7, 0, 1, 2, 3, 4, 6
ENABLE, HOME, MOVE_ABS, MOVE_VEL, STOP, RESET = 1, 2, 4, 8, 16, 32
HOMED, IN_POSITION, DRIVE_READY, MOVING = 1, 2, 32, 64
PLC_OWNED = {C + 11, *range(S, S + 15)}


@dataclass
class StubOptions:
    """Raw units (0.001 mm) — the C# simulator's defaults scaled by 1000."""

    port: int = 0
    unit: int = 1
    map_version: int = 1
    publish_limits: bool = True
    travel_min: int = 0
    travel_max: int = 10_000_000
    max_velocity: int = 500_000
    default_acceleration: int = 1_000_000
    quick_stop: int = 5_000_000
    homing_velocity: int = 50_000
    home_position: int = 0
    initial_position: int = 500_000
    watchdog_disabled: bool = False
    suppress_ack: bool = False
    swapped_word_order: bool = False
    watchdog_s: float = WATCHDOG_S
    """The FR-11 stall window; a PLC may trip anywhere in 1.0–1.5 s (ADR-31)."""
    stall_discrete: bool = False
    """MoveAbsolute is accepted (State 3) but the axis never moves: it never arrives (review #10)."""
    scan_s: float = SCAN_S
    """The PLC scan; a real PLC scans every 10–20 ms, a slow one more (review #5)."""

    @staticmethod
    def from_env(env: dict[str, str], port: int) -> StubOptions:
        o = StubOptions(port=port)
        keys = {
            "Simulator__MapVersion": "map_version",
            "Simulator__PublishLimits": "publish_limits",
            "Simulator__Faults__WatchdogDisabled": "watchdog_disabled",
            "Simulator__Faults__SuppressAck": "suppress_ack",
            "Simulator__Faults__SwappedWordOrder": "swapped_word_order",
        }
        types = {f.name: f.type for f in fields(StubOptions)}
        for key, name in keys.items():
            if key in env:
                raw = env[key]
                value: object = raw.lower() == "true" if types[name] in ("bool", bool) else int(raw)
                setattr(o, name, value)
        return o


@dataclass
class Axis:
    p: float
    v: float = 0.0
    state: int = STANDSTILL
    homed: bool = True
    in_position: bool = False
    fault: int = 0
    target: float = 0.0
    vcmd: float = 0.0
    accel: float = 0.0
    enable_blocked: bool = False
    armed: bool = False
    last_beat: int = 0
    beat_changed: float = field(default_factory=time.monotonic)


class StubPlc:
    def __init__(self, options: StubOptions | None = None) -> None:
        self.o = options or StubOptions()
        self.regs = [0] * 65536
        self.axis = Axis(p=float(self.o.initial_position), state=DISABLED)
        self._server: asyncio.Server | None = None
        self._scan: asyncio.Task[None] | None = None
        self._connections: set[asyncio.StreamWriter] = set()
        """Open client connections, closed on exit: a client the code under test leaked must not hang the teardown."""
        self.port = self.o.port
        self.writes: list[tuple[int, list[int]]] = []
        self.accepted: list[int] = []
        """Every command word the scan accepted, in order."""
        self.requests = 0
        """Requests received, answered or not."""
        self.drop_next = 0
        """Leave this many of the next requests unanswered (a lost answer: the client times out)."""
        self.drop_if: Callable[[bytes], bool] | None = None
        """Leave every request whose PDU matches unanswered (for example, every write of ``Heartbeat``)."""
        self._publish()

    # ----------------------------------------------------------------------------------------- lifecycle
    async def __aenter__(self) -> StubPlc:
        self._server = await asyncio.start_server(self._serve, "127.0.0.1", self.o.port)
        self.port = self._server.sockets[0].getsockname()[1]
        self._scan = asyncio.create_task(self._scan_loop())
        return self

    async def __aexit__(self, t: type[BaseException] | None, e: BaseException | None, tb: TracebackType | None) -> None:
        if self._scan is not None:
            self._scan.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._scan
        if self._server is not None:
            self._server.close()
            for writer in list(self._connections):
                writer.close()
            await self._server.wait_closed()

    # ----------------------------------------------------------------------------------------- Modbus TCP
    async def _serve(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self._connections.add(writer)
        try:
            while True:
                header = await reader.readexactly(7)
                tid, _pid, length, unit = struct.unpack(">HHHB", header)
                pdu = await reader.readexactly(length - 1)
                if unit != self.o.unit:
                    continue  # a unit nobody serves stays silent, as behind a gateway
                self.requests += 1
                if self.drop_next > 0:
                    self.drop_next -= 1
                    continue
                if self.drop_if is not None and self.drop_if(pdu):
                    continue
                reply = self._handle(pdu)
                writer.write(struct.pack(">HHHB", tid, 0, len(reply) + 1, unit) + reply)
                await writer.drain()
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        finally:
            self._connections.discard(writer)
            writer.close()

    def _handle(self, pdu: bytes) -> bytes:
        fc = pdu[0]
        if fc == 3:
            addr, count = struct.unpack(">HH", pdu[1:5])
            if addr + count > 65536:
                return bytes([fc | 0x80, 2])
            return bytes([fc, 2 * count]) + struct.pack(f">{count}H", *self.regs[addr : addr + count])
        if fc == 6:
            addr, value = struct.unpack(">HH", pdu[1:5])
            self._write(addr, [value])
            return pdu[:5]
        if fc == 16:
            addr, count = struct.unpack(">HH", pdu[1:5])
            values = list(struct.unpack(f">{count}H", pdu[6 : 6 + 2 * count]))
            self._write(addr, values)
            return pdu[:5]
        return bytes([fc | 0x80, 1])

    def _write(self, addr: int, values: list[int]) -> None:
        self.writes.append((addr, values))
        for i, value in enumerate(values):
            if addr + i not in PLC_OWNED:
                self.regs[addr + i] = value

    # ----------------------------------------------------------------------------------------- scan
    def i32(self, n: int) -> int:
        lo, hi = self.regs[n], self.regs[n + 1]
        if self.o.swapped_word_order:
            lo, hi = hi, lo
        u = lo | (hi << 16)
        return u - (1 << 32) if u & 0x80000000 else u

    def put32(self, n: int, value: int) -> None:
        u = value & 0xFFFFFFFF
        lo, hi = u & 0xFFFF, u >> 16
        self.regs[n], self.regs[n + 1] = (hi, lo) if self.o.swapped_word_order else (lo, hi)

    async def _scan_loop(self) -> None:
        last = time.monotonic()
        while True:
            await asyncio.sleep(self.o.scan_s)
            now = time.monotonic()
            self.scan(now - last, now)
            last = now

    def _error_stop(self, code: int) -> None:
        a = self.axis
        a.state, a.fault, a.v, a.enable_blocked = ERROR_STOP, code, 0.0, True

    def scan(self, dt: float, now: float) -> None:
        a, r = self.axis, self.regs
        # Watchdog (protocol.md § FR-11).
        beat, lease = r[C + 8], r[C + 9]
        if not self.o.watchdog_disabled:
            if lease == 0:
                a.armed = False
            elif beat != a.last_beat:
                a.beat_changed = now
                if r[C + 10] == 0:
                    a.armed = True
            if a.armed and now - a.beat_changed >= self.o.watchdog_s:
                self._error_stop(4)
                r[C + 10] = 1
                r[C + 11] = (r[C + 11] + 1) & 0xFFFF
                a.armed = False
        a.last_beat = beat

        # Accept a command write.
        word, seq = r[C], r[C + 1]
        if seq != r[S + 7] and not self.o.suppress_ack:
            self.accepted.append(word)
            self._accept(word)
            r[S + 7] = seq

        self._move(dt)
        self._publish()

    def _accept(self, word: int) -> None:
        a = self.axis
        if not word & ENABLE:
            if a.state != ERROR_STOP:
                a.enable_blocked = False
                a.state, a.v = DISABLED, 0.0
        elif a.state == DISABLED and not a.enable_blocked:
            a.state = STANDSTILL
        if word & STOP:
            if a.state in (HOMING, DISCRETE, CONTINUOUS):
                a.state = STOPPING
            return
        if word & RESET:
            if a.state == ERROR_STOP:
                a.state, a.fault = DISABLED, 0
                a.enable_blocked = bool(word & ENABLE)  # energise only on a fresh 0→1 after the Reset
            return
        if a.state not in (STANDSTILL, DISCRETE, CONTINUOUS):
            return  # a move with Enable low (or in a fault) is ignored
        acc = self.i32(C + 6)
        a.accel = float(acc if acc > 0 else self.o.default_acceleration)
        if word & HOME:
            a.state, a.in_position = HOMING, False
        elif word & MOVE_ABS and a.homed:
            a.state, a.in_position = DISCRETE, False
            a.target, a.vcmd = float(self.i32(C + 2)), float(abs(self.i32(C + 4)))
        elif word & MOVE_VEL:
            a.state, a.in_position = CONTINUOUS, False
            a.vcmd = float(self.i32(C + 4))

    def _slew(self, target_v: float, rate: float, dt: float) -> None:
        a = self.axis
        step = rate * dt
        a.v = target_v if abs(target_v - a.v) <= step else a.v + math.copysign(step, target_v - a.v)

    def _move(self, dt: float) -> None:
        a, o = self.axis, self.o
        if a.state == DISCRETE and o.stall_discrete:
            a.v = 0.0
        elif a.state == DISCRETE:
            d = a.target - a.p
            self._slew(math.copysign(min(a.vcmd, math.sqrt(2 * a.accel * abs(d))), d), a.accel, dt)
            a.p += a.v * dt
            if abs(a.target - a.p) <= 5 or (d > 0) != (a.target - a.p > 0):
                a.p, a.v, a.state, a.in_position = a.target, 0.0, STANDSTILL, True
        elif a.state == CONTINUOUS:
            self._slew(a.vcmd, a.accel, dt)
            a.p += a.v * dt
            if a.homed and not o.travel_min <= a.p <= o.travel_max:
                a.p, a.v, a.state = min(max(a.p, o.travel_min), o.travel_max), 0.0, STANDSTILL
        elif a.state == STOPPING:
            self._slew(0.0, o.quick_stop, dt)
            a.p += a.v * dt
            if a.v == 0:
                a.state = STANDSTILL
        elif a.state == HOMING:
            self._slew(-o.homing_velocity, a.accel, dt)
            a.p += a.v * dt
            if a.p <= o.home_position:
                a.p, a.v, a.homed, a.state = float(o.home_position), 0.0, True, STANDSTILL
        else:
            a.v = 0.0

    def _publish(self) -> None:
        a, o, r = self.axis, self.o, self.regs
        r[S] = a.state
        flags = (HOMED if a.homed else 0) | (IN_POSITION if a.in_position else 0)
        flags |= (DRIVE_READY if a.state != ERROR_STOP else 0) | (MOVING if a.v else 0)
        r[S + 1] = flags
        self.put32(S + 2, round(a.p))
        self.put32(S + 4, round(a.v))
        r[S + 6] = a.fault
        limits = (o.travel_min, o.travel_max, o.max_velocity) if o.publish_limits else (0, 0, 0)
        for offset, value in zip((8, 10, 12), limits, strict=True):
            self.put32(S + offset, value)
        r[S + 14] = o.map_version


async def _main(port: int) -> None:
    async with StubPlc(StubOptions.from_env(dict(os.environ), port)) as plc:
        print(f"stub PLC listening on 127.0.0.1:{plc.port}", flush=True)
        await asyncio.Event().wait()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--headless", action="store_true")
    parser.add_argument("--port", type=int, default=5020)
    args = parser.parse_args()
    with contextlib.suppress(KeyboardInterrupt):
        asyncio.run(_main(args.port))
    sys.exit(0)
