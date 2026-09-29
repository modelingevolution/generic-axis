"""``CheckContext``: what every check gets — the client, the map, the options, the beat, the command handshake and
the cleanup journal (design.md § Conformance checker, Python)."""

from __future__ import annotations

import contextlib
import time
from dataclasses import dataclass, field

from .beat import Beater
from .client import PlcClient, PlcError
from .lease import acquire
from .poll import PollResult, wait_for
from .registers import (
    COMMAND_LENGTH,
    EDGE_BITS,
    STATUS_LENGTH,
    AxisState,
    Command,
    RegisterMap,
    StatusBlock,
    next_nonzero,
    to_words,
)

ACK_TIMEOUT_S = 0.5
"""protocol.md § Command semantics, "Acknowledge": the driver waits ≤ 500 ms for ``CommandAck``."""

STATE_TIMEOUT_S = 5.0
"""CHK-06: State is 1 within 5 s after Enable 1 and 0 within 5 s after Enable 0."""

STOP_HALT_S = 0.2
"""protocol.md § Command semantics, "Stop": motion ceases within 200 ms of the write (CHK-14, CHK-15, CHK-16)."""

LEASE_TIMEOUT_S = 3.0
"""CHK-11 (b): the checker's lease client runs with a 3 s timeout. Used for every lease take."""

FOREIGN_OWNER_ID = 65534
"""protocol.md § Conformance checks, ``--owner-id``: CHK-11 uses 65534 as the foreign id (ADR-30)."""


@dataclass(frozen=True, slots=True)
class Options:
    """The protocol's command line (protocol.md § Conformance checks, "Command line")."""

    host: str
    port: int = 502
    unit: int = 1
    command_base: int = 0
    status_base: int = 100
    owner_id: int = 65535
    allow_motion: bool = False
    tolerance: float = 0.1


def command_label(word: int) -> str:
    """How a command write is named in messages: the edge it sets, else the Enable level."""
    for bit, name in (
        (Command.STOP, "Stop"),
        (Command.RESET, "Reset"),
        (Command.HOME, "Home"),
        (Command.MOVE_ABSOLUTE, "MoveAbsolute"),
        (Command.MOVE_VELOCITY, "MoveVelocity"),
    ):
        if word & bit:
            return name
    return f"Enable {word & Command.ENABLE}"


class AckTimeout(Exception):  # noqa: N818 — the protocol's name for the condition ("Acknowledge")
    """Protocol/NotAcknowledged: ``CommandAck`` did not echo ``CommandSeq`` within 500 ms."""

    def __init__(self, word: int, seq: int, status: StatusBlock, elapsed_ms: int) -> None:
        self.what = f"{command_label(word)} not accepted"
        self.detail = (
            f"CommandSeq {seq} written, CommandAck {status.command_ack} read after {elapsed_ms} ms, "
            f"State {status.state} read"
        )
        super().__init__(f"{self.what}. {self.detail}.")
        self.seq = seq
        self.status = status


@dataclass(slots=True)
class LastRead:
    """Raw C+0…C+11 and S+0…S+14; ``None`` for a register never read (protocol.md § Report schema, ``lastRead``)."""

    command: list[int | None]
    status: list[int | None]


@dataclass(slots=True)
class Ack:
    seq: int
    poll: PollResult
    written_at: float


@dataclass
class CheckContext:
    client: PlcClient
    registers: RegisterMap
    options: Options
    beater: Beater
    cleanup_log: list[str] = field(default_factory=list)
    seq: int | None = None
    command_word: int = 0
    holds_lease: bool = False
    """The checker holds the lease under its own id (protocol.md: from CHK-06 onwards)."""
    session_lease: bool = False
    """CHK-06 established the lease; every later check restores it."""
    caused_trip: bool = False
    connect_ms: int | None = None
    """How long the run's TCP connect took (CHK-01 ``connectMs``)."""
    evidence: LastRead | None = None
    """``lastRead`` a check took at detection because its next write would change what the FAIL is about."""

    # --- evidence (protocol.md § Error class of a FAIL, "lastRead") ---

    def shadow(self) -> LastRead:
        """The last value read from each register, ``None`` where none was read."""
        last = self.client.last_read
        base_c, base_s = self.registers.command_base, self.registers.status_base
        return LastRead(
            [last.get(base_c + i) for i in range(COMMAND_LENGTH)],
            [last.get(base_s + i) for i in range(STATUS_LENGTH)],
        )

    async def capture(self) -> LastRead:
        """A fresh read of both blocks (rule 5); if that read fails, the last values read."""
        if self.client.connected:
            with contextlib.suppress(PlcError):
                await self.client.read(self.registers.command_base, COMMAND_LENGTH)
                await self.client.read(self.registers.status_base, STATUS_LENGTH)
        return self.shadow()

    async def keep_evidence(self) -> None:
        """Capture ``lastRead`` now, before a write that would undo what the failure shows (review #2 c)."""
        self.evidence = await self.capture()

    # --- the command handshake (protocol.md § Command semantics, "Handshake") ---

    async def write_parameters(self, target: int, velocity: int, acceleration: int) -> None:
        """Registers C+2…C+7 in one FC16, before the command write."""
        words = [*to_words(target), *to_words(velocity), *to_words(acceleration)]
        await self.client.write(self.registers.target_position, words)

    async def _next_seq(self) -> int:
        if self.seq is None:
            # "Sequence at attach": continue from CommandAck + 1.
            self.seq = (await self.client.read_status(self.registers)).command_ack
        self.seq = next_nonzero(self.seq)
        return self.seq

    async def command(self, bits: Command) -> Ack:
        """Write ``[Command, CommandSeq]`` (C+0, C+1) with a new seq and wait ≤ 500 ms for the ack.

        On timeout the edge bits just set are cleared (protocol.md "Acknowledge") and ``AckTimeout`` is raised. After
        an ack the edge bits are cleared with the same seq, which is not a command write.
        """
        seq = await self._next_seq()
        word = int(bits)
        await self.client.write(self.registers.command, [word, seq], retry=False)  # a command is never re-sent
        written_at = time.monotonic()
        self.command_word = word
        poll = await wait_for(
            self.client, self.registers, lambda s: s.command_ack == seq, ACK_TIMEOUT_S, since=written_at
        )
        if not poll.met:
            await self.keep_evidence()  # C+0 still shows the edge that was not acknowledged
            if word & EDGE_BITS:
                await self.clear_edges()
            raise AckTimeout(word, seq, poll.status, poll.elapsed_ms)
        if word & EDGE_BITS:
            await self.clear_edges()
        return Ack(seq, poll, written_at)

    async def clear_edges(self) -> None:
        """Clear edge bits, keep Enable, keep the seq (protocol.md: this write does not increment ``CommandSeq``)."""
        if self.seq is None:
            raise RuntimeError("clear_edges before any command write: there is no CommandSeq to keep")
        self.command_word &= int(Command.ENABLE)
        await self.client.write(self.registers.command, [self.command_word, self.seq])

    @property
    def enabled(self) -> Command:
        return Command.ENABLE if self.command_word & Command.ENABLE else Command.NONE

    async def status(self) -> StatusBlock:
        return await self.client.read_status(self.registers)

    async def watchdog(self) -> tuple[int, int]:
        """``(WatchdogFault, WatchdogTrips)`` — registers C+10, C+11."""
        fault, trips = await self.client.read(self.registers.watchdog_fault, 2)
        return fault, trips

    # --- lease and beat ---

    async def take_lease(self) -> None:
        await acquire(self.client, self.registers, self.options.owner_id, LEASE_TIMEOUT_S)
        self.holds_lease = True

    async def release_lease(self) -> None:
        """Clean release (protocol.md § FR-11): stop beating, then ``LeaseOwner = 0``."""
        await self.beater.stop()
        await self.client.write(self.registers.lease_owner, [0])
        self.holds_lease = False

    async def clear_watchdog_fault(self) -> None:
        await self.client.write(self.registers.watchdog_fault, [0])

    async def recover(self) -> StatusBlock:
        """Leave ErrorStop and any motion: Stop edge while moving, Reset + ``WatchdogFault = 0`` after a fault.

        Enable is dropped by the Reset write, since the PLC energises only on a fresh 0→1 (protocol.md "Enable").
        """
        status = await self.status()
        if status.state in (
            AxisState.HOMING,
            AxisState.DISCRETE_MOTION,
            AxisState.CONTINUOUS_MOTION,
            AxisState.STOPPING,
        ):
            if status.state != AxisState.STOPPING:
                await self.command(self.enabled | Command.STOP)
            status = (
                await wait_for(
                    self.client,
                    self.registers,
                    lambda s: s.state in (AxisState.STANDSTILL, AxisState.DISABLED, AxisState.ERROR_STOP),
                    STATE_TIMEOUT_S,
                )
            ).status
        fault, _ = await self.watchdog()
        if fault:
            await self.clear_watchdog_fault()
        if status.state == AxisState.ERROR_STOP or status.state not in (AxisState.DISABLED, AxisState.STANDSTILL):
            await self.command(Command.RESET)
            status = await self.status()
        return status
