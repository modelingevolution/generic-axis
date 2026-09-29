"""protocol.md § Errors and debugging: message shape (rule 1) and the FaultCode → class table."""

from __future__ import annotations

import pytest

from generic_axis_check.context import AckTimeout, command_label
from generic_axis_check.errors import ErrorClass, Read, format_message, machine_error, state_is_invalid
from generic_axis_check.registers import Command, RegisterMap, StatusBlock, describe_range, register_ref

MAP = RegisterMap()


def status(state: int, fault: int, ack: int = 0) -> StatusBlock:
    return StatusBlock.parse([state, 0, 0, 0, 0, 0, fault, ack, 0, 0, 0, 0, 0, 0, 1])


def test_format_message_matches_the_protocol_examples() -> None:
    assert (
        format_message(ErrorClass.PROTOCOL, "ProtocolMismatch", "attach refused", MAP, [Read("MapVersion", 2, 1)])
        == "Protocol/ProtocolMismatch: attach refused. Read MapVersion (S+14 = 114) = 2, expected 1."
    )
    reads = [Read("FaultCode", 4), Read("WatchdogFault", 1), Read("WatchdogTrips", 3)]
    assert format_message(ErrorClass.MACHINE, "WatchdogTripped", "tripped", MAP, reads) == (
        "Machine/WatchdogTripped: tripped. Read FaultCode (S+6 = 106) = 4, WatchdogFault (C+10 = 10) = 1, "
        "WatchdogTrips (C+11 = 11) = 3."
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


def test_register_refs_follow_the_bases() -> None:
    assert register_ref(MAP, "MapVersion") == ("S+14", 114)
    assert register_ref(RegisterMap(200, 300), "Heartbeat") == ("C+8", 208)
    assert describe_range(MAP, 100, 15) == "S+0…S+14 (100…114)"
    assert describe_range(MAP, 9, 1) == "C+9 (9)"
    assert describe_range(MAP, 50, 2) == "50…51"
