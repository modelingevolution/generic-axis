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
from .checks import CHECKS, FAIL, OBSERVED, PASS, RETRIES_KEY, SKIPPED, Check, Outcome
from .client import PlcClient, PlcError
from .context import ACK_TIMEOUT_S, AckTimeout, CheckContext, Options
from .errors import (
    COMMUNICATION_LOST,
    NOT_ACKNOWLEDGED,
    PROTOCOL_MISMATCH,
    WATCHDOG_TRIPPED,
    ErrorClass,
    Read,
    format_message,
    machine_error,
)
from .lease import LeaseHeld
from .poll import ms, wait_for
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

INTERRUPTED = "INTERRUPTED"

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
    observed: dict[str, int | None] = field(default_factory=dict)
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
    unknown_observed: set[str] = field(default_factory=set)
    """``check id.key`` a check reported that § Observed values does not list (dropped; a defect tests catch)."""

    @property
    def failed(self) -> int:
        return sum(c.result == FAIL for c in self.checks)

    @property
    def result(self) -> str:
        """protocol.md § Report schema: ``INTERRUPTED`` if the operator interrupted the run, otherwise ``FAIL`` if
        any check failed, otherwise ``PASS``."""
        if self.interrupted:
            return INTERRUPTED
        return FAIL if self.failed else PASS

    @property
    def exit_code(self) -> int:
        """protocol.md: 0 no FAIL · 1 at least one FAIL · 3 refused to start · 4 interrupted (2 is argparse's)."""
        if self.refused:
            return 3
        if self.interrupted:
            return 4
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


async def preflight(client: PlcClient, registers: RegisterMap) -> str | None:
    """protocol.md "Pre-flight": watch ``Heartbeat`` for 1 s before the tool's own first beat. Any change, whatever
    ``LeaseOwner`` holds, is a live commander: returns what was seen, or None. Writes nothing (ADR-33)."""
    beat, owner = await client.read(registers.heartbeat, 2)
    beats = [beat]
    deadline = time.monotonic() + PREFLIGHT_WATCH_S
    while time.monotonic() < deadline:
        await asyncio.sleep(BEAT_PERIOD_S)
        now_beat, owner = await client.read(registers.heartbeat, 2)
        if now_beat != beats[-1]:
            beats.append(now_beat)
    if len(beats) == 1:
        return None
    seen = " → ".join(str(b) for b in beats)
    return (
        f"pre-flight: another commander is live: Heartbeat (C+8) changed {seen} within {PREFLIGHT_WATCH_S:g} s, "
        f"LeaseOwner (C+9) {owner}; stop it first"
    )


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
            seq = ctx.seq = next_nonzero(ctx.seq if ctx.seq is not None else status.command_ack)
            word = int(ctx.enabled | Command.STOP)
            await ctx.client.write(registers.command, [word, seq], retry=False)  # a command is never re-sent
            written_at = time.monotonic()
            ctx.command_word = word
            journal.append(f"C+0 = 0x{word:04X}, C+1 = {seq} (Stop)")
            # protocol.md "Command block": the edge is cleared after CommandAck echoes CommandSeq. Clearing it sooner
            # can land before the PLC scan that sees the edge, and the Stop is never executed (review #5).
            ack = await wait_for(ctx.client, registers, lambda s: s.command_ack == seq, ACK_TIMEOUT_S, since=written_at)
            if not ack.met:
                journal.append(
                    f"Stop: no CommandAck within {ms(ACK_TIMEOUT_S)} ms (CommandSeq {seq} written, CommandAck "
                    f"{ack.status.command_ack} read, State {ack.status.state} read)"
                )

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
    run_started = time.monotonic()
    results: dict[str, CheckResult] = {}
    refused = interrupted = False
    proven_free = False
    """Pre-flight proved no other commander beats. Until then the tool has written nothing, so cleanup has nothing
    to undo and must write nothing either: the axis may belong to a live commander (ADR-33, review #18)."""
    abort: str | None = None
    unknown: set[str] = set()

    running = "pre-flight"
    preflight_failure: Outcome | None = None
    try:
        try:
            connect_started = time.monotonic()
            await client.connect()
            ctx.connect_ms = ms(time.monotonic() - connect_started)
            live = await preflight(client, registers)
            proven_free = live is None
        except PlcError as exc:
            # Review #4: pre-flight did not prove the axis free, so nothing may be written. CHK-01 FAILs with the
            # Transport error and every other check is SKIPPED; the run never proceeds as if the axis were free.
            live = None
            what = f"pre-flight did not complete, nothing was written: {exc}"
            preflight_failure = Outcome(
                FAIL,
                format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, what, registers),
                {},
                ErrorClass.TRANSPORT,
            )
            abort = "needs CHK-01, which FAILED"
            say(f"CHK-01: {preflight_failure.message}")
        if live is not None:
            refused = True
            abort = live
            say(abort)

        for check in checks:
            if preflight_failure is not None and check.id == "CHK-01":
                observed = normalize_observed(check.id, {"connectMs": ctx.connect_ms}, client.retries, unknown)
                results[check.id] = CheckResult(
                    check.id,
                    check.title,
                    check.section,
                    FAIL,
                    ms(time.monotonic() - run_started),
                    preflight_failure.message,
                    observed,
                    ErrorClass.TRANSPORT,
                    await capture(ctx),
                )
                continue
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
            running = check.id
            began = time.monotonic()
            # CHK-01 is the run's transport: its ``retries`` include the connect and pre-flight retries before it.
            retries_before = 0 if check.id == "CHK-01" else client.retries
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
                # § Error class of a FAIL, "Interruption is not a FAIL": this check and every later one SKIPPED.
                task = asyncio.current_task()
                if task is not None:
                    task.uncancel()
                interrupted = True
                abort = f"interrupted by the operator during {check.id}"
                results[check.id] = _skip(check, abort)
                say(abort)
                continue
            observed = normalize_observed(check.id, outcome.observed, client.retries - retries_before, unknown)
            result = CheckResult(
                check.id,
                check.title,
                check.section,
                outcome.result,
                ms(time.monotonic() - began),
                outcome.message,
                observed,
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
            results.setdefault(check.id, _skip(check, f"interrupted by the operator during {running}"))
    finally:
        if client.connected and proven_free:
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
        unknown_observed=unknown,
    )


def normalize_observed(
    check_id: str, observed: dict[str, int | None], retries: int, unknown: set[str]
) -> dict[str, int | None]:
    """§ Observed values: exactly the listed keys in order, ``null`` where never observed, then ``retries``."""
    keys = OBSERVED[check_id]
    unknown.update(f"{check_id}.{key}" for key in observed if key not in keys)
    return {**{key: observed.get(key) for key in keys}, RETRIES_KEY: retries}


def exception_outcome(exc: PlcError | AckTimeout | LeaseHeld, registers: RegisterMap) -> Outcome:
    """One cause, one class (protocol.md § Errors and debugging, rule 2)."""
    if isinstance(exc, AckTimeout):
        message = format_message(ErrorClass.PROTOCOL, NOT_ACKNOWLEDGED, exc.what, registers, (), exc.detail)
        return Outcome(FAIL, message, {}, ErrorClass.PROTOCOL)  # the seq, ack and state are in the message
    if isinstance(exc, LeaseHeld):
        # No Commander class in a checker FAIL: pre-flight proved nobody else beats, so a lease that stays held
        # means Heartbeat kept changing or LeaseOwner did not hold what was written (§ Error class of a FAIL).
        read = Read("LeaseOwner", exc.owner)
        what = f"{exc}: Heartbeat kept changing or LeaseOwner did not hold what was written"
        message = format_message(ErrorClass.PROTOCOL, PROTOCOL_MISMATCH, what, registers, (read,))
        return Outcome(FAIL, message, {}, ErrorClass.PROTOCOL)
    message = format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, str(exc), registers)
    return Outcome(FAIL, message, {}, ErrorClass.TRANSPORT)


async def _run_one(check: Check, ctx: CheckContext) -> Outcome:
    ctx.client.guard = ctx.beater.raise_if_failed  # a dead beat is Transport, never a Machine trip (review #7)
    try:
        outcome = await check.run(ctx)
        if outcome.result == FAIL and outcome.motion_error == WATCHDOG_TRIPPED:
            # The guard ran before the read that showed the trip, but that read queued behind the beat's retry on
            # the one pymodbus connection: the beat can have died while it waited. Then the trip's cause is the lost
            # link, and the FAIL says so (rule 2).
            ctx.beater.raise_if_failed()
        return outcome
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return exception_outcome(exc, ctx.registers)
    finally:
        ctx.client.guard = None  # evidence, restore and cleanup must still reach the PLC


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
