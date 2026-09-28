"""The register map of protocol.md (map version 1), its 32-bit codec and the parsed status block.

Written from ``docs/protocol.md`` alone (design.md § Conformance checker, ADR-29): nothing here is copied from the
C# driver or simulator.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import IntEnum, IntFlag

MAP_VERSION = 1
"""protocol.md § Transport, "Map version": register S+14 holds 1 for this document."""

COMMAND_LENGTH = 12
"""protocol.md § Command block: holding registers C+0 … C+11."""

STATUS_LENGTH = 15
"""protocol.md § Status block: holding registers S+0 … S+14, read in one FC03."""

REGISTER_SPACE = 65536
"""Modbus holding-register addresses are 16-bit."""

WORD_MASK = 0xFFFF
INT32_MIN = -(2**31)
INT32_MAX = 2**31 - 1


class Command(IntFlag):
    """protocol.md § Command block, register C+0 ``Command``."""

    NONE = 0
    ENABLE = 1 << 0
    HOME = 1 << 1
    MOVE_ABSOLUTE = 1 << 2
    MOVE_VELOCITY = 1 << 3
    STOP = 1 << 4
    RESET = 1 << 5


EDGE_BITS = Command.HOME | Command.MOVE_ABSOLUTE | Command.MOVE_VELOCITY | Command.STOP | Command.RESET
"""protocol.md: bits 1–5 are edge-triggered, bit 0 is a level."""


class Flags(IntFlag):
    """protocol.md § Status block, register S+1 ``Flags``."""

    NONE = 0
    HOMED = 1 << 0
    IN_POSITION = 1 << 1
    LIMIT_MIN = 1 << 2
    LIMIT_MAX = 1 << 3
    HOME_SENSOR = 1 << 4
    DRIVE_READY = 1 << 5
    MOVING = 1 << 6


class AxisState(IntEnum):
    """protocol.md § Status block, register S+0 ``State`` (5 is reserved)."""

    DISABLED = 0
    STANDSTILL = 1
    HOMING = 2
    DISCRETE_MOTION = 3
    CONTINUOUS_MOTION = 4
    STOPPING = 6
    ERROR_STOP = 7


VALID_STATES = frozenset(int(s) for s in AxisState)
"""protocol.md: any other ``State`` value is read as ErrorStop; CHK-04 requires one of these."""

MOVING_STATES = frozenset({AxisState.HOMING, AxisState.DISCRETE_MOTION, AxisState.CONTINUOUS_MOTION})
"""protocol.md § Conformance checks, cleanup rule: Stop edge if State is 2, 3 or 4."""


class FaultCode(IntEnum):
    """protocol.md § Status block, register S+6 ``FaultCode`` (100+ vendor-specific)."""

    NONE = 0
    DRIVE = 1
    LIMIT_SWITCH = 2
    FOLLOWING_ERROR = 3
    WATCHDOG = 4
    HOMING_FAILED = 5
    DRIVE_LINK_LOST = 6
    SAFETY_STOP = 7


@dataclass(frozen=True, slots=True)
class RegisterMap:
    """Absolute addresses of every register for a given pair of block bases (protocol.md § Transport)."""

    command_base: int = 0
    status_base: int = 100

    def __post_init__(self) -> None:
        for name, base, length in (
            ("command", self.command_base, COMMAND_LENGTH),
            ("status", self.status_base, STATUS_LENGTH),
        ):
            if base < 0 or base + length > REGISTER_SPACE:
                raise ValueError(f"{name} block {base}..{base + length - 1} is outside 0..{REGISTER_SPACE - 1}")
        c_end = self.command_base + COMMAND_LENGTH
        s_end = self.status_base + STATUS_LENGTH
        if self.command_base < s_end and self.status_base < c_end:
            raise ValueError(
                f"command block {self.command_base}..{c_end - 1} overlaps status block {self.status_base}..{s_end - 1}"
            )

    # Command block (C+n).
    @property
    def command(self) -> int:
        return self.command_base + 0

    @property
    def command_seq(self) -> int:
        return self.command_base + 1

    @property
    def target_position(self) -> int:
        return self.command_base + 2

    @property
    def velocity(self) -> int:
        return self.command_base + 4

    @property
    def acceleration(self) -> int:
        return self.command_base + 6

    @property
    def heartbeat(self) -> int:
        return self.command_base + 8

    @property
    def lease_owner(self) -> int:
        return self.command_base + 9

    @property
    def watchdog_fault(self) -> int:
        return self.command_base + 10

    @property
    def watchdog_trips(self) -> int:
        return self.command_base + 11

    # Status block (S+n).
    @property
    def status(self) -> int:
        return self.status_base + 0

    @property
    def travel_limits(self) -> int:
        return self.status_base + 8

    @property
    def map_version(self) -> int:
        return self.status_base + 14


def to_words(value: int) -> tuple[int, int]:
    """Encode an int32 as ``(low, high)`` registers, two's complement (protocol.md § Transport, word order)."""
    if not INT32_MIN <= value <= INT32_MAX:
        raise ValueError(f"{value} does not fit int32")
    unsigned = value & 0xFFFFFFFF
    return unsigned & WORD_MASK, unsigned >> 16


def from_words(low: int, high: int) -> int:
    """Decode ``(low, high)`` registers into an int32 (protocol.md § Transport, word order)."""
    unsigned = (low & WORD_MASK) | ((high & WORD_MASK) << 16)
    return unsigned - (1 << 32) if unsigned & 0x80000000 else unsigned


def next_nonzero(value: int) -> int:
    """The next ``Heartbeat`` or ``CommandSeq`` value: never 0, 65535 wraps to 1 (protocol.md § Command block)."""
    return 1 if value >= WORD_MASK else value + 1


@dataclass(frozen=True, slots=True)
class StatusBlock:
    """One FC03 read of S+0 … S+14 (protocol.md § Status block). Positions and velocities are raw register values."""

    state: int
    flags: Flags
    actual_position: int
    actual_velocity: int
    fault_code: int
    command_ack: int
    travel_min: int
    travel_max: int
    max_velocity: int
    map_version: int

    @staticmethod
    def parse(registers: list[int]) -> StatusBlock:
        if len(registers) != STATUS_LENGTH:
            raise ValueError(f"status block needs {STATUS_LENGTH} registers, got {len(registers)}")
        r = registers
        return StatusBlock(
            state=r[0],
            flags=Flags(r[1]),
            actual_position=from_words(r[2], r[3]),
            actual_velocity=from_words(r[4], r[5]),
            fault_code=r[6],
            command_ack=r[7],
            travel_min=from_words(r[8], r[9]),
            travel_max=from_words(r[10], r[11]),
            max_velocity=from_words(r[12], r[13]),
            map_version=r[14],
        )

    @property
    def homed(self) -> bool:
        return Flags.HOMED in self.flags

    @property
    def in_position(self) -> bool:
        return Flags.IN_POSITION in self.flags


REGISTERS: tuple[tuple[str, str, int, bool], ...] = (
    # (name, block, offset, int32) — protocol.md § Command block and § Status block.
    ("Command", "C", 0, False),
    ("CommandSeq", "C", 1, False),
    ("TargetPosition", "C", 2, True),
    ("Velocity", "C", 4, True),
    ("Acceleration", "C", 6, True),
    ("Heartbeat", "C", 8, False),
    ("LeaseOwner", "C", 9, False),
    ("WatchdogFault", "C", 10, False),
    ("WatchdogTrips", "C", 11, False),
    ("State", "S", 0, False),
    ("Flags", "S", 1, False),
    ("ActualPosition", "S", 2, True),
    ("ActualVelocity", "S", 4, True),
    ("FaultCode", "S", 6, False),
    ("CommandAck", "S", 7, False),
    ("TravelMin", "S", 8, True),
    ("TravelMax", "S", 10, True),
    ("MaxVelocity", "S", 12, True),
    ("MapVersion", "S", 14, False),
)
_BY_NAME = {name: (block, offset) for name, block, offset, _ in REGISTERS}


def register_ref(registers: RegisterMap, name: str) -> tuple[str, int]:
    """``("S+14", 114)`` for ``"MapVersion"`` with the default bases."""
    block, offset = _BY_NAME[name]
    base = registers.command_base if block == "C" else registers.status_base
    return f"{block}+{offset}", base + offset


def describe_range(registers: RegisterMap, address: int, count: int) -> str:
    """``S+0…S+14 (100…114)`` for a range inside a block, else the absolute range."""
    last = address + count - 1
    span = f"{address}" if count == 1 else f"{address}…{last}"
    for block, base, length in (
        ("C", registers.command_base, COMMAND_LENGTH),
        ("S", registers.status_base, STATUS_LENGTH),
    ):
        if base <= address and last < base + length:
            rel = f"{block}+{address - base}" if count == 1 else f"{block}+{address - base}…{block}+{last - base}"
            return f"{rel} ({span})"
    return span
