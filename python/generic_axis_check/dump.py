"""``--dump [--watch]``: the decoded register dump (protocol.md § Errors and debugging, rule 4).

Two tables, one per block and register space (ADR-36): the command block in holding registers (FC03) and the status
block in input registers (FC04). One row per register: its address in the protocol's notation (``S+14 = input 14``), name,
raw hex, and the decoded value. ``State`` and ``FaultCode`` by name, ``Flags`` and ``Command`` bits by name, 32-bit
values in engineering units on their low word.
"""

from __future__ import annotations

import asyncio
import time
from collections.abc import Callable, Sequence
from datetime import UTC, datetime

from .client import PlcClient
from .registers import (
    BLOCK_SPACE,
    COMMAND_LENGTH,
    REGISTERS,
    STATUS_LENGTH,
    VALID_STATES,
    AxisState,
    Command,
    FaultCode,
    Flags,
    RegisterMap,
    from_words,
)

WATCH_PERIOD_S = 0.2
"""Rule 4: ``--watch`` repeats the dump at 5 Hz until Ctrl-C."""

UNIT_NOTE = "u = the axis unit (mm for a linear axis, ° for a rotary axis); raw values are 0.001 u"

_ENGINEERING = {
    "TargetPosition": "u",
    "ActualPosition": "u",
    "TravelMin": "u",
    "TravelMax": "u",
    "Velocity": "u/s",
    "ActualVelocity": "u/s",
    "MaxVelocity": "u/s",
    "Acceleration": "u/s²",
}
_STATE_NAMES = {
    AxisState.DISABLED: "Disabled",
    AxisState.STANDSTILL: "Standstill",
    AxisState.HOMING: "Homing",
    AxisState.DISCRETE_MOTION: "DiscreteMotion",
    AxisState.CONTINUOUS_MOTION: "ContinuousMotion",
    AxisState.STOPPING: "Stopping",
    AxisState.ERROR_STOP: "ErrorStop",
}
_FAULT_NAMES = {
    FaultCode.NONE: "none",
    FaultCode.DRIVE: "drive fault",
    FaultCode.LIMIT_SWITCH: "limit switch tripped",
    FaultCode.FOLLOWING_ERROR: "following error",
    FaultCode.WATCHDOG: "watchdog",
    FaultCode.HOMING_FAILED: "homing failed",
    FaultCode.DRIVE_LINK_LOST: "communication to drive lost",
    FaultCode.SAFETY_STOP: "safety stop",
}
_COMMAND_BITS = (
    (Command.ENABLE, "Enable"),
    (Command.HOME, "Home"),
    (Command.MOVE_ABSOLUTE, "MoveAbsolute"),
    (Command.MOVE_VELOCITY, "MoveVelocity"),
    (Command.STOP, "Stop"),
    (Command.RESET, "Reset"),
)
_FLAG_BITS = (
    (Flags.HOMED, "Homed"),
    (Flags.IN_POSITION, "InPosition"),
    (Flags.LIMIT_MIN, "LimitMin"),
    (Flags.LIMIT_MAX, "LimitMax"),
    (Flags.HOME_SENSOR, "HomeSensor"),
    (Flags.DRIVE_READY, "DriveReady"),
    (Flags.MOVING, "Moving"),
)


def _bits(value: int, names: Sequence[tuple[int, str]], width: int = 16) -> str:
    set_names = [name for bit, name in names if value & bit]
    known = 0
    for bit, _ in names:
        known |= int(bit)  # int: inverting an IntFlag keeps only its defined bits and would hide unknown ones
    unknown = value & ~known & ((1 << width) - 1)
    if unknown:
        set_names.append(f"unknown bits 0x{unknown:04X}")
    return " | ".join(set_names) if set_names else "none"


def _decode(name: str, value: int) -> str:
    match name:
        case "Command":
            return _bits(value, _COMMAND_BITS)
        case "Flags":
            return _bits(value, _FLAG_BITS)
        case "State":
            label = _STATE_NAMES.get(AxisState(value)) if value in VALID_STATES else None
            return f"{value} {label}" if label else f"{value} (invalid: Protocol/ProtocolMismatch)"
        case "FaultCode":
            if value >= 100:
                return f"{value} vendor-specific"
            label = next((text for code, text in _FAULT_NAMES.items() if code == value), None)
            return f"{value} {label}" if label else f"{value} (undefined)"
        case "LeaseOwner":
            return f"{value} (unowned)" if value == 0 else str(value)
        case "WatchdogFault":
            return {0: "0 healthy", 1: "1 tripped"}.get(value, f"{value} (undefined)")
        case _:
            return str(value)


_TABLES = (
    ("C", "Command block: holding registers (FC03 read, FC06/FC16 write), C"),
    ("S", "Status block: input registers (FC04 read), S"),
)


def render(registers: RegisterMap, command: Sequence[int | None], status: Sequence[int | None]) -> str:
    """The dump of holding C+0…C+11 and input S+0…S+14, one table each. ``None`` marks a register never read."""
    words: dict[tuple[str, int], int | None] = {}
    for offset in range(COMMAND_LENGTH):
        words["C", offset] = command[offset]
    for offset in range(STATUS_LENGTH):
        words["S", offset] = status[offset]
    rows: list[str] = []
    for table, title in _TABLES:
        base = registers.command_base if table == "C" else registers.status_base
        rows += [
            *([""] if rows else []),
            f"{title} = {base}",
            f"{'Address':<18} {'Register':<22} {'Raw':<7} Decoded",
        ]
        rows += _rows(table, base, words)
    return "\n".join([*rows, UNIT_NOTE])


def _rows(table: str, base: int, words: dict[tuple[str, int], int | None]) -> list[str]:
    rows: list[str] = []
    space = BLOCK_SPACE[table]
    for name, block, offset, is_int32 in REGISTERS:
        if block != table:
            continue
        cells = [(offset, name)] + ([(offset + 1, f"{name} (high)")] if is_int32 else [])
        for index, (off, label) in enumerate(cells):
            raw = words[block, off]
            address = f"{block}+{off} = {space} {base + off}"  # the protocol's notation, one column (review #35)
            if raw is None:
                rows.append(f"{address:<18} {label:<22} {'—':<7} not read")
                continue
            if is_int32 and index == 0:
                high = words[block, off + 1]
                if high is None:
                    decoded = "high word not read"
                else:
                    value = from_words(raw, high)
                    decoded = f"{value} = {value / 1000:.3f} {_ENGINEERING[name]}"
            elif is_int32:
                decoded = "high word"
            else:
                decoded = _decode(name, raw)
            rows.append(f"{address:<18} {label:<22} 0x{raw:04X}  {decoded}")
    return rows


async def read_blocks(client: PlcClient, registers: RegisterMap) -> tuple[list[int], list[int]]:
    command = await client.read(registers.command_base, COMMAND_LENGTH)  # FC03, holding
    status = await client.read_input(registers.status_base, STATUS_LENGTH)  # FC04, input
    return command, status


async def dump(client: PlcClient, registers: RegisterMap, out: Callable[[str], None], *, watch: bool = False) -> None:
    """Read both blocks and print them; with ``watch`` repeat at 5 Hz until cancelled. Writes nothing."""
    next_at = time.monotonic()
    while True:
        command, status = await read_blocks(client, registers)
        stamp = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"
        out(f"{client.host}:{client.port} unit {client.unit} at {stamp}\n{render(registers, command, status)}\n")
        if not watch:
            return
        next_at += WATCH_PERIOD_S
        await asyncio.sleep(max(0.0, next_at - time.monotonic()))
