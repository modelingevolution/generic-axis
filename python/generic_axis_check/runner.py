"""The run: pre-flight, id order, prerequisite skipping, per-check restore, and cleanup that always happens
(protocol.md § Conformance checks, "Rules for every run")."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime

from .beat import BEAT_PERIOD_S, Beater
from .checks import CHECKS, FAIL, PASS, SKIPPED, Check, Outcome
from .client import PlcClient, PlcError
from .context import AckTimeout, CheckContext, Options
from .errors import (
    CANCELLED,
    COMMUNICATION_LOST,
    LEASE_HELD,
    NOT_ACKNOWLEDGED,
    PROTOCOL_MISMATCH,
    ErrorClass,
    Read,
    format_message,
    machine_error,
)
from .lease import LeaseHeld
from .poll import ms
from .registers import (
    COMMAND_LENGTH,
    EDGE_BITS,
    MOVING_STATES,
    STATUS_LENGTH,
    AxisState,
    Command,
    FaultCode,
    RegisterMap,
    next_nonzero,
)

PREFLIGHT_WATCH_S = 1.0
"""protocol.md "Pre-flight": read ``LeaseOwner`` and watch ``Heartbeat`` for 1 s."""

FIRST_LEASED_CHECK = "CHK-06"
"""protocol.md "Lease and beat between checks": from CHK-06 onwards the checker holds the lease and beats."""

log = logging.getLogger(__name__)


@dataclass(slots=True)
class LastRead:
    """Raw C+0…C+11 and S+0…S+14; ``None`` for a register never read (protocol.md § Report schema, ``lastRead``)."""

    command: list[int | None]
    status: list[int | None]


@dataclass(slots=True)
class CheckResult:
    id: str
    title: str
    section: str
    result: str
    duration_ms: int
    message: str
    observed: dict[str, int] = field(default_factory=dict)
    error_class: ErrorClass | None = None
    last_read: LastRead | None = None


@dataclass(slots=True)
class Report:
    options: Options
    started_at: datetime
    finished_at: datetime
    checks: list[CheckResult]
    cleanup: list[str]
    refused: bool = False
    interrupted: bool = False

    @property
    def failed(self) -> int:
        return sum(c.result == FAIL for c in self.checks)

    @property
    def result(self) -> str:
        """protocol.md § Report schema: ``FAIL`` if any check failed, otherwise ``PASS``."""
        return FAIL if self.failed else PASS

    @property
    def exit_code(self) -> int:
        """protocol.md: 0 = no FAIL · 1 = at least one FAIL · 3 = refused to start (2 is argparse's usage error)."""
        if self.refused:
            return 3
        return 1 if self.failed else 0


Progress = Callable[[str], None]


def _skip(check: Check, message: str) -> CheckResult:
    return CheckResult(check.id, check.title, check.section, SKIPPED, 0, message)


def prerequisite_problem(check: Check, results: dict[str, CheckResult]) -> str | None:
    """The first unmet prerequisite, named with its result (protocol.md "Order")."""
    for need in check.needs:
        prior = results.get(f"CHK-{need}")
        if prior is None or prior.result != PASS:
            state = "FAILED" if prior is not None and prior.result == FAIL else "SKIPPED"
            return f"needs CHK-{need}, which {state}"
    return None


async def preflight(client: PlcClient, registers: RegisterMap, owner_id: int) -> int | None:
    """Return the owner id of a live foreign commander, or None. Writes nothing (ADR-33)."""
    beat, owner = await client.read(registers.heartbeat, 2)
    if owner in (0, owner_id):
        return None
    deadline = time.monotonic() + PREFLIGHT_WATCH_S
    while time.monotonic() < deadline:
        await asyncio.sleep(BEAT_PERIOD_S)
        now_beat, now_owner = await client.read(registers.heartbeat, 2)
        if now_owner != owner:
            return now_owner if now_owner not in (0, owner_id) else None
        if now_beat != beat:
            return owner
    return None


async def restore(ctx: CheckContext) -> Outcome | None:
    """protocol.md "Each check restores": State 0 or 1, no latched fault, lease held and beat running.

    Returns a FAIL outcome saying why it could not, or None.
    """
    await ctx.recover()
    ctx.caused_trip = False
    if ctx.session_lease:
        (owner,) = await ctx.client.read(ctx.registers.lease_owner, 1)
        if owner != ctx.options.owner_id:
            await ctx.take_lease()
        ctx.holds_lease = True
        await ctx.beater.start()
    fault, _ = await ctx.watchdog()
    status = await ctx.status()
    if status.state not in (AxisState.DISABLED, AxisState.STANDSTILL) or status.fault_code != FaultCode.NONE:
        error_class, motion_error = machine_error(status)
        reads = (Read("State", status.state), Read("FaultCode", status.fault_code))
        message = format_message(
            error_class, motion_error, "restore: the axis stayed out of State 0/1", ctx.registers, reads
        )
        return Outcome(FAIL, message, {}, error_class)
    if fault:
        read = Read("WatchdogFault", fault, 0)
        message = format_message(
            ErrorClass.PROTOCOL,
            PROTOCOL_MISMATCH,
            "restore: WatchdogFault stayed 1 after " "writing 0",
            ctx.registers,
            (read,),
        )
        return Outcome(FAIL, message, {}, ErrorClass.PROTOCOL)
    return None


async def cleanup(ctx: CheckContext) -> None:
    """protocol.md "Cleanup, always": Stop if moving · clear edges · Enable 0 · WatchdogFault 0 · release lease.

    Every write is journaled in ``ctx.cleanup_log``; a failing step is journaled and the next one still runs.
    """
    journal = ctx.cleanup_log
    registers = ctx.registers

    async def step(label: str, action: Callable[[], object]) -> None:
        try:
            result = action()
            if asyncio.iscoroutine(result):
                await result
        except (PlcError, AckTimeout, OSError) as exc:
            journal.append(f"{label}: failed ({exc})")

    async def stop_if_moving() -> None:
        status = await ctx.status()
        if status.state in MOVING_STATES:
            ctx.seq = next_nonzero(ctx.seq if ctx.seq is not None else status.command_ack)
            word = int(ctx.enabled | Command.STOP)
            await ctx.client.write(registers.command, [word, ctx.seq])
            ctx.command_word = word
            journal.append(f"C+0 = 0x{word:04X}, C+1 = {ctx.seq} (Stop)")

    async def clear_edges() -> None:
        (word,) = await ctx.client.read(registers.command, 1)
        if word & EDGE_BITS and ctx.seq is not None:
            kept = word & int(Command.ENABLE)
            await ctx.client.write(registers.command, [kept, ctx.seq])
            ctx.command_word = kept
            journal.append(f"C+0 = 0x{kept:04X} (clear edge bits)")

    async def enable_off() -> None:
        (word,) = await ctx.client.read(registers.command, 1)
        if word & Command.ENABLE:
            ctx.seq = next_nonzero(ctx.seq if ctx.seq is not None else (await ctx.status()).command_ack)
            await ctx.client.write(registers.command, [0, ctx.seq])
            ctx.command_word = 0
            journal.append(f"C+0 = 0x0000, C+1 = {ctx.seq} (Enable 0)")

    async def clear_trip() -> None:
        fault, _ = await ctx.watchdog()
        if fault and (ctx.caused_trip or ctx.holds_lease):
            await ctx.client.write(registers.watchdog_fault, [0])
            journal.append("C+10 = 0 (clear the watchdog fault the checker caused)")

    async def release() -> None:
        (owner,) = await ctx.client.read(registers.lease_owner, 1)
        if owner == ctx.options.owner_id:
            await ctx.client.write(registers.lease_owner, [0])
            journal.append("C+9 = 0 (release lease)")
        ctx.holds_lease = False

    await step("Stop", stop_if_moving)
    await step("clear edge bits", clear_edges)
    await step("Enable 0", enable_off)
    await step("stop beating", ctx.beater.stop)
    await step("WatchdogFault = 0", clear_trip)
    await step("release lease", release)


async def run(options: Options, progress: Progress | None = None, checks: tuple[Check, ...] = CHECKS) -> Report:
    """Run the checklist against ``options.host``. Never raises for a PLC problem: every problem is a FAIL."""
    say = progress or (lambda _line: None)
    registers = RegisterMap(options.command_base, options.status_base)
    client = PlcClient(options.host, options.port, options.unit, registers)
    ctx = CheckContext(client, registers, options, Beater(client, registers))
    started_at = datetime.now(UTC)
    results: dict[str, CheckResult] = {}
    refused = interrupted = False
    abort: str | None = None

    try:
        try:
            await client.connect()
            foreign = await preflight(client, registers, options.owner_id)
        except PlcError as exc:
            foreign = None  # CHK-01 reports the transport failure
            say(f"pre-flight: {exc}")
        if foreign is not None:
            refused = True
            abort = f"pre-flight: LeaseOwner {foreign} is beating: another commander is attached; stop it first"
            say(abort)

        for check in checks:
            if abort is not None:
                results[check.id] = _skip(check, abort)
                continue
            if check.motion and not options.allow_motion:
                results[check.id] = _skip(check, "needs --allow-motion")
                continue
            problem = prerequisite_problem(check, results)
            if problem is not None:
                results[check.id] = _skip(check, problem)
                continue
            say(f"{check.id} {check.title} ...")
            began = time.monotonic()
            last_read: LastRead | None = None
            try:
                outcome = await _run_one(check, ctx)
                if outcome.result == FAIL:
                    last_read = await capture(ctx)
                if check.id >= FIRST_LEASED_CHECK:
                    why = await _restore(ctx)
                    if why is not None:
                        if outcome.result != FAIL:
                            outcome = why
                            last_read = ctx_last_read(ctx)
                        else:
                            outcome.message += f" Restore failed: {why.message}"
                        abort = f"restore after {check.id} failed"
            except asyncio.CancelledError:
                task = asyncio.current_task()
                if task is not None:
                    task.uncancel()
                interrupted = True
                abort = "interrupted"
                message = format_message(
                    ErrorClass.COMMANDER, CANCELLED, "interrupted (Ctrl-C) during this check", registers
                )
                outcome = Outcome(FAIL, message, {}, ErrorClass.COMMANDER)
                last_read = ctx_last_read(ctx)
            result = CheckResult(
                check.id,
                check.title,
                check.section,
                outcome.result,
                ms(time.monotonic() - began),
                outcome.message,
                outcome.observed,
                outcome.error_class,
                last_read,
            )
            results[check.id] = result
            if result.result == FAIL:
                say(f"{check.id}: {result.message} ({result.duration_ms} ms)")
            else:
                say(f"{check.id} {result.result} ({result.duration_ms} ms) {result.message}")
    except asyncio.CancelledError:
        task = asyncio.current_task()
        if task is not None:
            task.uncancel()
        interrupted = True
        for check in checks:
            results.setdefault(check.id, _skip(check, "interrupted"))
    finally:
        if client.connected and not refused:
            await cleanup(ctx)
        with contextlib.suppress(PlcError):
            await ctx.beater.stop()  # already stopped by cleanup unless the connection was lost
        client.close()

    return Report(
        options,
        started_at,
        datetime.now(UTC),
        [results[c.id] for c in checks],
        ctx.cleanup_log,
        refused=refused,
        interrupted=interrupted,
    )


def exception_outcome(exc: PlcError | AckTimeout | LeaseHeld, registers: RegisterMap) -> Outcome:
    """One cause, one class (protocol.md § Errors and debugging, rule 2)."""
    if isinstance(exc, AckTimeout):
        message = format_message(ErrorClass.PROTOCOL, NOT_ACKNOWLEDGED, exc.what, registers, (), exc.detail)
        observed = {"commandSeq": exc.seq, "commandAck": exc.status.command_ack, "state": exc.status.state}
        return Outcome(FAIL, message, observed, ErrorClass.PROTOCOL)
    if isinstance(exc, LeaseHeld):
        read = Read("LeaseOwner", exc.owner)
        message = format_message(ErrorClass.COMMANDER, LEASE_HELD, str(exc), registers, (read,))
        return Outcome(FAIL, message, {"leaseOwner": exc.owner}, ErrorClass.COMMANDER)
    message = format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, str(exc), registers)
    return Outcome(FAIL, message, {}, ErrorClass.TRANSPORT)


async def _run_one(check: Check, ctx: CheckContext) -> Outcome:
    try:
        return await check.run(ctx)
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return exception_outcome(exc, ctx.registers)


def ctx_last_read(ctx: CheckContext) -> LastRead:
    """The last value read from each register, ``None`` where none was read."""
    shadow = ctx.client.last_read
    base_c, base_s = ctx.registers.command_base, ctx.registers.status_base
    return LastRead(
        [shadow.get(base_c + i) for i in range(COMMAND_LENGTH)],
        [shadow.get(base_s + i) for i in range(STATUS_LENGTH)],
    )


async def capture(ctx: CheckContext) -> LastRead:
    """Evidence for a FAIL (rule 5): read both blocks now, before any restore write; if that read fails, fall back
    to the last values read."""
    if ctx.client.connected:
        with contextlib.suppress(PlcError):
            await ctx.client.read(ctx.registers.command_base, COMMAND_LENGTH)
            await ctx.client.read(ctx.registers.status_base, STATUS_LENGTH)
    return ctx_last_read(ctx)


async def _restore(ctx: CheckContext) -> Outcome | None:
    if not ctx.client.connected:
        message = format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, "restore: not connected", ctx.registers)
        return Outcome(FAIL, message, {}, ErrorClass.TRANSPORT)
    try:
        why = await restore(ctx)
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return exception_outcome(exc, ctx.registers)
    return why
