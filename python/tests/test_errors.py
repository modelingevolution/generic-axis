"""protocol.md § Errors and debugging: message shape (rule 1) and the FaultCode → class table."""

from __future__ import annotations

import re
from pathlib import Path

import pytest

from generic_axis_check.context import AckTimeout, command_label
from generic_axis_check.errors import (
    MOTION_ERROR_CLASSES,
    ErrorClass,
    Read,
    class_of,
    format_message,
    machine_error,
    state_is_invalid,
)
from generic_axis_check.registers import (
    REGISTERS,
    Command,
    RegisterMap,
    Space,
    StatusBlock,
    describe_range,
    register_ref,
)

MAP = RegisterMap()
PROTOCOL = Path(__file__).resolve().parents[2] / "docs" / "protocol.md"

REGISTER_NAMES = {name for name, _block, _offset, _unit in REGISTERS}


def status(state: int, fault: int, ack: int = 0) -> StatusBlock:
    return StatusBlock.parse([state, 0, 0, 0, 0, 0, fault, ack, 0, 0, 0, 0, 0, 0, 1])


def test_format_message_matches_the_protocol_examples() -> None:
    assert (
        format_message(ErrorClass.PROTOCOL, "ProtocolMismatch", "attach refused", MAP, [Read("MapVersion", 2, 1)])
        == "Protocol/ProtocolMismatch: attach refused. Read MapVersion (S+14 = input 14) = 2, expected 1."
    )
    reads = [Read("FaultCode", 4), Read("WatchdogFault", 1), Read("WatchdogTrips", 3)]
    assert format_message(ErrorClass.MACHINE, "WatchdogTripped", "tripped", MAP, reads) == (
        "Machine/WatchdogTripped: tripped. Read FaultCode (S+6 = input 6) = 4, WatchdogFault (C+10 = holding 10) = 1, "
        "WatchdogTrips (C+11 = holding 11) = 3."
    )


def test_ack_timeout_message_is_the_protocol_example() -> None:
    exc = AckTimeout(int(Command.ENABLE), 7, status(0, 0, ack=6), 500)
    message = format_message(ErrorClass.PROTOCOL, "NotAcknowledged", exc.what, MAP, (), exc.detail)
    assert message == (
        "Protocol/NotAcknowledged: Enable 1 not accepted. CommandSeq 7 written, CommandAck 6 read after 500 ms, "
        "State 0 read."
    )


@pytest.mark.parametrize(
    ("word", "label"),
    [
        (0, "Enable 0"),
        (1, "Enable 1"),
        (3, "Home"),
        (5, "MoveAbsolute"),
        (9, "MoveVelocity"),
        (17, "Stop"),
        (32, "Reset"),
    ],
)
def test_command_label_names_the_edge_else_the_enable_level(word: int, label: str) -> None:
    assert command_label(word) == label


@pytest.mark.parametrize(
    ("fault", "expected"),
    [
        (1, (ErrorClass.MACHINE, "DriveFault")),
        (2, (ErrorClass.MACHINE, "LimitTripped")),
        (3, (ErrorClass.MACHINE, "MotionFailed")),
        (4, (ErrorClass.MACHINE, "WatchdogTripped")),
        (5, (ErrorClass.MACHINE, "HomeLatchFailed")),
        (6, (ErrorClass.MACHINE, "DriveFault")),
        (7, (ErrorClass.MACHINE, "SafetyStop")),
        (101, (ErrorClass.MACHINE, "DriveFault")),
        (0, (ErrorClass.PROTOCOL, "ProtocolMismatch")),
        (42, (ErrorClass.PROTOCOL, "ProtocolMismatch")),
    ],
)
def test_machine_error_follows_the_fault_code_table(fault: int, expected: tuple[ErrorClass, str]) -> None:
    assert machine_error(status(7, fault)) == expected


@pytest.mark.parametrize(
    ("state", "fault", "invalid"), [(5, 0, True), (8, 0, True), (7, 0, True), (7, 4, False), (1, 0, False)]
)
def test_state_is_invalid_for_5_above_7_and_errorstop_without_fault(state: int, fault: int, invalid: bool) -> None:
    assert state_is_invalid(status(state, fault)) is invalid


def test_register_refs_name_the_offset_and_the_typed_absolute_register() -> None:
    # GA-U-133.py (ADR-36, rule 1): "<address> is the offset and the absolute register with its type: C+n = holding a,
    # S+n = input a (a = base + n)". Both blocks at 0 are told apart only by the type.
    assert register_ref(MAP, "MapVersion") == "S+14 = input 14"
    assert register_ref(MAP, "WatchdogFault") == "C+10 = holding 10"
    assert register_ref(RegisterMap(200, 300), "Heartbeat") == "C+8 = holding 208"
    assert register_ref(RegisterMap(200, 300), "FaultCode") == "S+6 = input 306"
    assert describe_range(MAP, Space.INPUT, 0, 15) == "S+0…S+14 = input 0…14"
    assert describe_range(MAP, Space.HOLDING, 0, 12) == "C+0…C+11 = holding 0…11"
    assert describe_range(MAP, Space.HOLDING, 9, 1) == "C+9 = holding 9"
    assert describe_range(RegisterMap(0, 100), Space.INPUT, 108, 6) == "S+8…S+13 = input 108…113"
    # The same absolute range in the other space is not the block: holding 100 is not S+0 when S = input 100.
    assert describe_range(RegisterMap(0, 100), Space.HOLDING, 100, 15) == "holding 100…114"
    assert describe_range(MAP, Space.INPUT, 50, 2) == "input 50…51"


def _class_rows() -> dict[str, str]:
    """protocol.md § Errors and debugging: the class table, ``{class: its SDK MotionError cell}``."""
    text = PROTOCOL.read_text(encoding="utf-8")
    rows = re.findall(r"^\| \*\*(Transport|Protocol|Machine|Commander)\*\* \| [^|]+ \| ([^|]+) \|", text, re.M)
    return dict(rows)


def test_every_motion_error_maps_to_the_class_row_that_names_it() -> None:
    # GA-U-66.py (review #2 a): the map equals protocol.md's table — each member in its row, each member of a row mapped.
    rows = _class_rows()
    assert set(rows) == {c.value for c in ErrorClass}
    for class_name, cell in rows.items():
        named = {n for n in re.findall(r"`([A-Z][A-Za-z]+)`", cell) if n not in REGISTER_NAMES}
        mapped = {name for name, c in MOTION_ERROR_CLASSES.items() if c == class_name}
        assert named == mapped, class_name
    assert class_of("UnknownAxis") == ErrorClass.COMMANDER
    assert class_of("WrongAxisKind") == ErrorClass.COMMANDER


def test_a_message_cannot_name_a_motion_error_under_another_class() -> None:
    # Rule 2, "one cause, one class": the builder refuses a Transport WatchdogTripped.
    with pytest.raises(ValueError, match="WatchdogTripped is Machine, not Transport"):
        format_message(ErrorClass.TRANSPORT, "WatchdogTripped", "tripped", MAP)
