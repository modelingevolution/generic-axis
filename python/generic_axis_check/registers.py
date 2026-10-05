"""The register map of protocol.md (map version 1), its 32-bit codec and the parsed status block.

Written from ``docs/protocol.md`` alone (design.md § Conformance checker, ADR-29): nothing here is copied from the
C# driver or simulator.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from enum import IntEnum, IntFlag, StrEnum

MAP_VERSION = 1
"""protocol.md § Transport, "Map version": register S+14 holds 1 for this document."""

COMMAND_LENGTH = 12
"""protocol.md § Command block: holding registers C+0 … C+11."""

STATUS_LENGTH = 15
"""protocol.md § Status block: input registers S+0 … S+14, read in one FC04."""

REGISTER_SPACE = 65536
"""Modbus register addresses are 16-bit, in each space (holding and input) separately."""


class Space(StrEnum):
    """protocol.md § Transport, "Register type": the command block is holding registers, the status block input
    registers, each block in its own address space (ADR-36)."""

    HOLDING = "holding"
    INPUT = "input"


BLOCK_SPACE = {"C": Space.HOLDING, "S": Space.INPUT}
"""The space of each block: ``C+n`` is holding register C+n, ``S+n`` is input register S+n."""

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
"""protocol.md § Status block: any other ``State`` value is a Protocol error (``ProtocolMismatch``); CHK-04 requires one
of these."""

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
    """Absolute addresses of every register for a given pair of block bases (protocol.md § Transport, ADR-36).

    ``command_base`` is a holding-register address, ``status_base`` an input-register address: the two blocks live in
    different spaces, so they cannot overlap and both default to 0.
    """

    command_base: int = 0
    status_base: int = 0

    def __post_init__(self) -> None:
        for name, base, length in (
            ("command", self.command_base, COMMAND_LENGTH),
            ("status", self.status_base, STATUS_LENGTH),
        ):
            space = BLOCK_SPACE[name[0].upper()]
            if base < 0 or base + length > REGISTER_SPACE:
                raise ValueError(
                    f"{name} block {space} {base}..{base + length - 1} is outside {space} 0..{REGISTER_SPACE - 1}"
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


def round_half_away(value: float) -> int:
    """protocol.md "Speed rounding": round half away from zero (2.5 → 3, −2.5 → −3), never banker's, never floored."""
    rounded = math.floor(abs(value) + 0.5)
    return -rounded if value < 0 and rounded else rounded


def speed_raw(max_velocity: int, percent: float) -> int:
    """protocol.md § One-verb mode step 3, "Speed rounding" (both modes, review #65): raw = round-half-away-from-zero(
    pct × MaxVelocity raw ÷ 100). A 0 is refused by the caller, never floored to 1."""
    return round_half_away(percent * max_velocity / 100)


def format_percent(value: float) -> str:
    """A percentage as "0.###" (both tools): 150 → 150, 12.5 → 12.5, 0.00009 → 0."""
    return f"{value:.3f}".rstrip("0").rstrip(".")


def next_nonzero(value: int) -> int:
    """The next ``Heartbeat`` or ``CommandSeq`` value: never 0, 65535 wraps to 1 (protocol.md § Command block)."""
    return 1 if value >= WORD_MASK else value + 1


@dataclass(frozen=True, slots=True)
class StatusBlock:
    """One FC04 read of S+0 … S+14 (protocol.md § Status block). Positions and velocities are raw register values."""

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


def _block(registers: RegisterMap, block: str) -> tuple[Space, int, int]:
    if block == "C":
        return Space.HOLDING, registers.command_base, COMMAND_LENGTH
    return Space.INPUT, registers.status_base, STATUS_LENGTH


def register_ref(registers: RegisterMap, name: str) -> str:
    """``"S+14 = input 14"`` for ``"MapVersion"`` with the default bases: the offset and the absolute register with its
    type (protocol.md § Errors and debugging, rule 1)."""
    block, offset = _BY_NAME[name]
    space, base, _length = _block(registers, block)
    return f"{block}+{offset} = {space} {base + offset}"


def describe_range(registers: RegisterMap, space: Space, address: int, count: int) -> str:
    """``S+0…S+14 = input 0…14`` for a range inside a block of ``space``, else ``holding 50…51``."""
    last = address + count - 1
    span = f"{address}" if count == 1 else f"{address}…{last}"
    for block in ("C", "S"):
        block_space, base, length = _block(registers, block)
        if block_space == space and base <= address and last < base + length:
            rel = f"{block}+{address - base}" if count == 1 else f"{block}+{address - base}…{block}+{last - base}"
            return f"{rel} = {space} {span}"
    return f"{space} {span}"
