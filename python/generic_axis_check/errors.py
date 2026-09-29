"""protocol.md § Errors and debugging: four error classes and the say-what-you-saw message shape.

    <CHK-nn>: <Class>/<MotionError>: <what happened>. Read <Register> (<address>) = <value>[, expected <value>].

The message carried in a report starts at ``<Class>``; progress lines and logs prefix the check id.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from enum import StrEnum

from .registers import AxisState, FaultCode, RegisterMap, StatusBlock, register_ref


class ErrorClass(StrEnum):
    TRANSPORT = "Transport"
    PROTOCOL = "Protocol"
    MACHINE = "Machine"
    COMMANDER = "Commander"


# SDK MotionError names used by the checker (protocol.md § Errors and debugging, class table).
COMMUNICATION_LOST = "CommunicationLost"
PROTOCOL_MISMATCH = "ProtocolMismatch"
NOT_ACKNOWLEDGED = "NotAcknowledged"
DRIVE_FAULT = "DriveFault"
LIMIT_TRIPPED = "LimitTripped"
MOTION_FAILED = "MotionFailed"
WATCHDOG_TRIPPED = "WatchdogTripped"
HOME_LATCH_FAILED = "HomeLatchFailed"
SAFETY_STOP = "SafetyStop"
LEASE_HELD = "LeaseHeld"

VENDOR_FAULT_BASE = 100
"""protocol.md § Status block: ``FaultCode`` 100+ is vendor-specific."""

_FAULT_ERRORS = {
    FaultCode.DRIVE: DRIVE_FAULT,
    FaultCode.LIMIT_SWITCH: LIMIT_TRIPPED,
    FaultCode.FOLLOWING_ERROR: MOTION_FAILED,
    FaultCode.WATCHDOG: WATCHDOG_TRIPPED,
    FaultCode.HOMING_FAILED: HOME_LATCH_FAILED,
    FaultCode.DRIVE_LINK_LOST: DRIVE_FAULT,
    FaultCode.SAFETY_STOP: SAFETY_STOP,
}


@dataclass(frozen=True, slots=True)
class Read:
    """One register value the message cites: ``name`` is the protocol's register name."""

    name: str
    value: int
    expected: int | None = None


def _sentence(text: str) -> str:
    return text.rstrip(". ") + "."


def format_message(
    error_class: ErrorClass,
    motion_error: str,
    what: str,
    registers: RegisterMap,
    reads: Sequence[Read] = (),
    detail: str | None = None,
) -> str:
    parts = [f"{error_class}/{motion_error}: {_sentence(what)}"]
    if detail:
        parts.append(_sentence(detail))
    if reads:
        cited = []
        for r in reads:
            ref, address = register_ref(registers, r.name)
            text = f"{r.name} ({ref} = {address}) = {r.value}"
            if r.expected is not None:
                text += f", expected {r.expected}"
            cited.append(text)
        parts.append(_sentence("Read " + ", ".join(cited)))
    return " ".join(parts)


def machine_error(status: StatusBlock) -> tuple[ErrorClass, str]:
    """The class and MotionError a reported fault maps to (protocol.md § Errors and debugging, class table).

    ``State = 7`` with ``FaultCode = 0``, and codes the protocol does not define, are Protocol mismatches.
    """
    code = status.fault_code
    if code >= VENDOR_FAULT_BASE:
        return ErrorClass.MACHINE, DRIVE_FAULT
    for fault, error in _FAULT_ERRORS.items():
        if code == fault:
            return ErrorClass.MACHINE, error
    return ErrorClass.PROTOCOL, PROTOCOL_MISMATCH


def state_is_invalid(status: StatusBlock) -> bool:
    """``State`` 5 or above 7, or ``State = 7`` with ``FaultCode = 0`` (ProtocolMismatch)."""
    if status.state not in {int(s) for s in AxisState}:
        return True
    return status.state == AxisState.ERROR_STOP and status.fault_code == FaultCode.NONE
