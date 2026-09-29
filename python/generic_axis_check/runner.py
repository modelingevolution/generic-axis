"""The run: pre-flight, id order, prerequisite skipping, per-check restore, and cleanup that always happens
(protocol.md § Conformance checks, "Rules for every run")."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections.abc import Callable, Coroutine
from dataclasses import dataclass, field
from datetime import UTC, datetime

from .beat import BEAT_PERIOD_S, Beater, LeaseLost
from .checks import CHECKS, FAIL, OBSERVED, PASS, RETRIES_KEY, SKIPPED, Check, Outcome
from .client import PlcClient, PlcError
from .context import ACK_TIMEOUT_S, AckTimeout, CheckContext, LastRead, Options
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
    EDGE_BITS,
    MAP_VERSION,
    MOVING_STATES,
    AxisState,
    Command,
    FaultCode,
    RegisterMap,
    next_nonzero,
)

INTERRUPTED = "INTERRUPTED"
REFUSED = "REFUSED"

PREFLIGHT_WATCH_S = 1.0
"""protocol.md "Pre-flight": read ``LeaseOwner`` and watch ``Heartbeat`` for 1 s."""

HELD_LEASE_WATCH_S = 1.6
"""protocol.md "Pre-flight": with ``LeaseOwner ≠ 0``, 1.5 s (the latest FR-11 trip) plus one 100 ms read."""

FIRST_LEASED_CHECK = "CHK-06"
"""protocol.md "Lease and beat between checks": from CHK-06 onwards the checker holds the lease and beats."""

log = logging.getLogger(__name__)


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
    preflight: str | None = None
    """The pre-flight refusal, or its note when the run proceeded (a dead commander); the Markdown's "Pre-flight:"
    line after the heading."""
    unknown_observed: set[str] = field(default_factory=set)
    """``check id.key`` a check reported that § Observed values does not list (dropped; a defect tests catch)."""

    @property
    def failed(self) -> int:
        return sum(c.result == FAIL for c in self.checks)

    @property
    def result(self) -> str:
        """protocol.md § Report schema: ``REFUSED`` if pre-flight refused to start, otherwise ``INTERRUPTED`` if the
        operator interrupted the run, otherwise ``FAIL`` if any check failed, otherwise ``PASS`` (review #22)."""
        if self.refused:
            return REFUSED
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


@dataclass(frozen=True, slots=True)
class Preflight:
    """The pre-flight verdict: a refusal (exit 3), or a note for the report when the run proceeds (or neither)."""

    refusal: str | None = None
    note: str | None = None
    foreign_trip: bool = False
    """The lease holder is dead and its watchdog trip is left for its operator: the checker never clears it."""


async def preflight(client: PlcClient, registers: RegisterMap) -> Preflight:
    """protocol.md "Pre-flight" (review #35): before the tool's own first beat, watch ``Heartbeat`` for 1 s; with
    ``LeaseOwner ≠ 0`` also watch ``WatchdogFault`` for 1.6 s. A ``Heartbeat`` change is a live commander (refused,
    whatever ``LeaseOwner`` holds). A held lease with ``WatchdogFault = 1`` and no beat is a dead commander (proceed,
    and say so). A held lease with neither is refused. Writes nothing (ADR-33)."""
    beat, owner, fault = await client.read(registers.heartbeat, 3)
    beats = [beat]
    started = time.monotonic()

    def watching() -> bool:
        elapsed = time.monotonic() - started
        if len(beats) > 1:
            return elapsed < PREFLIGHT_WATCH_S  # a beat is still watched to 1 s so the refusal names several values
        if owner != 0 and fault != 0:
            # Review #25: a trip does not make its holder dead. Only a full 1 s with no Heartbeat change does; a live
            # commander still beating after a trip must be refused, not overwritten.
            return elapsed < PREFLIGHT_WATCH_S
        return elapsed < (PREFLIGHT_WATCH_S if owner == 0 else HELD_LEASE_WATCH_S)

    while watching():
        await asyncio.sleep(BEAT_PERIOD_S)
        now_beat, owner, fault = await client.read(registers.heartbeat, 3)
        if now_beat != beats[-1]:
            beats.append(now_beat)
    watched_s = time.monotonic() - started  # review #27: messages name the window actually watched
    owner_at = f"LeaseOwner (C+9 = {registers.lease_owner}) = {owner}"
    fault_at = f"WatchdogFault (C+10 = {registers.watchdog_fault}) = {fault}"
    if len(beats) > 1:
        seen = " → ".join(str(b) for b in beats)
        return Preflight(
            # Review #28: rule 1's register shape; no "pre-flight:" label (the Markdown line adds "Pre-flight: ").
            f"another commander is live: Heartbeat (C+8 = {registers.heartbeat}) = {seen} within {watched_s:.1f} s, "
            f"{owner_at}; stop it first"
        )
    if owner == 0:
        return Preflight()
    if fault != 0:
        note = (
            f"{owner_at} held with no beat and {fault_at}: the previous commander is dead; its trip is left for its "
            "operator."
        )
        return Preflight(note=note, foreign_trip=True)
    return Preflight(
        f"{owner_at} is held and {fault_at}: no beat and no trip within {watched_s:.1f} s — "
        "a live commander, or a PLC without a working watchdog; release LeaseOwner by hand only if no commander runs"
    )


async def to_completion(step: Coroutine[object, object, None]) -> None:
    """Run ``step`` to its end even if this task is cancelled meanwhile (review #26: cleanup must complete under a
    repeated Ctrl-C; a half-done cleanup can leave Enable | Stop in C+0 and the lease held)."""
    inner = asyncio.ensure_future(step)
    while not inner.done():
        try:
            await asyncio.shield(inner)
        except asyncio.CancelledError:
            current = asyncio.current_task()
            if current is not None:
                current.uncancel()
    inner.result()


async def hold_from_preflight(ctx: CheckContext) -> None:
    """protocol.md § Rules, "Lease and beat between checks" (review #35 b): from the end of pre-flight the checker
    holds the lease under its own id and beats, so a second tool is refused from CHK-01 on. Only on a PLC whose
    ``MapVersion`` reads 1 (a wrong map version is never written), and never over a dead holder's trip."""
    try:
        (version,) = await ctx.client.read(ctx.registers.map_version, 1)
        if version != MAP_VERSION:
            return
        await ctx.take_lease()
        await ctx.beater.start()
    except (PlcError, LeaseHeld) as exc:
        log.warning("taking the lease after pre-flight failed (%s); CHK-01 reports the transport", exc)


async def foreign_trip_problem(ctx: CheckContext) -> Outcome | None:
    """protocol.md "Pre-flight", dead commander: "The tool never clears that trip: a restore that finds it FAILs
    Machine/WatchdogTripped". Reads only."""
    if not ctx.foreign_trip or ctx.caused_trip:
        return None
    fault, _ = await ctx.watchdog()
    if fault == 0:
        return None
    (owner,) = await ctx.client.read(ctx.registers.lease_owner, 1)
    what = "the previous commander's watchdog trip is left for its operator; the checker does not clear it"
    reads = (Read("WatchdogFault", fault), Read("LeaseOwner", owner))
    message = format_message(ErrorClass.MACHINE, WATCHDOG_TRIPPED, what, ctx.registers, reads)
    return Outcome(FAIL, message, {}, ErrorClass.MACHINE, WATCHDOG_TRIPPED, restore=False)


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
    if ctx.beater.lease_lost is not None:
        # Lead ruling #33: the axis has another owner. Stop our own beat; write nothing else to it.
        await ctx.beater.stop()
        journal.append("C+8 (Heartbeat): stopped beating")
        log.warning("cleanup: %s Nothing else is written to an axis the checker no longer owns.", ctx.beater.lease_lost)
        return

    async def step(label: str, action: Callable[[], object]) -> None:
        try:
            result = action()
            if asyncio.iscoroutine(result):
                await result
        except (PlcError, AckTimeout, OSError) as exc:
            journal.append(f"{label}: failed ({exc})")
        except Exception as exc:  # review #14: the next cleanup step still runs
            log.exception("cleanup %s: checker defect", label)
            journal.append(f"{label}: failed (checker defect: {type(exc).__name__}: {exc})")

    # Nothing is written that the checker did not change: Stop and Enable 0 only once it wrote a command itself (a
    # dead commander's Enable and motion are left for its operator, #35 and #18).
    commanded = ctx.seq is not None

    async def stop_if_moving() -> None:
        status = await ctx.status()
        if commanded and status.state in MOVING_STATES:
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
        if commanded and word & Command.ENABLE:
            ctx.seq = next_nonzero(ctx.seq if ctx.seq is not None else (await ctx.status()).command_ack)
            await ctx.client.write(registers.command, [0, ctx.seq])
            ctx.command_word = 0
            journal.append(f"C+0 = 0x0000, C+1 = {ctx.seq} (Enable 0)")

    async def clear_trip() -> None:
        fault, _ = await ctx.watchdog()
        if fault and (ctx.caused_trip or (ctx.holds_lease and not ctx.foreign_trip)):
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
    ctx.beater.expected_owner = lambda: options.owner_id if ctx.holds_lease else None
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
    preflight_text: str | None = None
    try:
        try:
            connect_started = time.monotonic()
            await client.connect()
            ctx.connect_ms = ms(time.monotonic() - connect_started)
            verdict = await preflight(client, registers)
            live = verdict.refusal
            preflight_text = verdict.refusal or verdict.note
            ctx.foreign_trip = verdict.foreign_trip
            if verdict.note is not None:
                say(f"pre-flight: {verdict.note}")
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
        except Exception as exc:  # review #14: a checker defect before anything was written
            log.exception("pre-flight: checker defect")
            live = None
            preflight_failure = defect_outcome(exc, "pre-flight (nothing was written)")
            abort = "needs CHK-01, which FAILED"
            say(f"CHK-01: {preflight_failure.message}")
        if live is not None:
            refused = True
            abort = live
            say(f"pre-flight: {abort}")
        elif proven_free and not ctx.foreign_trip:
            await hold_from_preflight(ctx)

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
                    preflight_failure.error_class,
                    await ctx.capture(),
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
                ctx.evidence = None
                outcome = await _run_one(check, ctx)
                if outcome.result == FAIL:
                    # § Error class of a FAIL, "lastRead": the read the check took at detection, before any write
                    # of its own undid the evidence (an edge clear, CHK-11's recovery); else a fresh read now.
                    last_read = ctx.evidence or await ctx.capture()
                lost = ctx.beater.lease_lost
                if lost is not None:
                    # A check may have caught the guard's exception itself: the lost lease still fails it. No
                    # restore; every later check is SKIPPED with the reason; cleanup only stops the beat.
                    lost_outcome = lease_lost_outcome(lost, ctx.registers)
                    outcome = Outcome(FAIL, lost_outcome.message, outcome.observed, ErrorClass.PROTOCOL)
                    last_read = last_read or await ctx.capture()
                    abort = f"{check.id}: {lost_outcome.message.split(': ', 1)[1]}"
                elif outcome.defect:
                    abort = f"not run: checker error during {check.id}"
                elif not outcome.restore:
                    # protocol.md "Each check restores": a precondition FAIL is a failure to restore. Nothing Resets an
                    # axis the checker did not fault, so nothing more runs (CHK-11 included; #9, lead ruling).
                    abort = f"restore after {check.id} failed"
                elif check.id >= FIRST_LEASED_CHECK and (foreign := await foreign_trip_problem(ctx)) is not None:
                    # A dead commander's trip is still latched: nothing may restore or clear it, so nothing more runs.
                    if outcome.result != FAIL:
                        outcome, last_read = foreign, await ctx.capture()
                    elif outcome.message != foreign.message:
                        outcome.message += f" Restore failed: {foreign.message}"
                    abort = f"restore after {check.id} failed"
                elif check.id >= FIRST_LEASED_CHECK and outcome.restore:
                    why = await _restore(ctx)
                    if why is not None:
                        if outcome.result != FAIL:
                            outcome = why
                            last_read = ctx.shadow()
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
            await to_completion(cleanup(ctx))
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
        preflight=preflight_text,
    )


def normalize_observed(
    check_id: str, observed: dict[str, int | None], retries: int, unknown: set[str]
) -> dict[str, int | None]:
    """§ Observed values: exactly the listed keys in order, ``null`` where never observed, then ``retries``."""
    keys = OBSERVED[check_id]
    extra = [key for key in observed if key not in keys]
    if extra:
        # Review #23: never dropped silently. A key outside the table is a checker defect, not a PLC finding, so it
        # is logged at Error (and kept in Report.unknown_observed for the tests) instead of failing the PLC's check.
        log.error("%s: observed key(s) outside § Observed values, not reported: %s", check_id, ", ".join(extra))
        unknown.update(f"{check_id}.{key}" for key in extra)
    return {**{key: observed.get(key) for key in keys}, RETRIES_KEY: retries}


def lease_lost_outcome(lost: LeaseLost, registers: RegisterMap) -> Outcome:
    """protocol.md § Rules for every run, "Lease and beat between checks" (lead ruling #33)."""
    read = Read("LeaseOwner", lost.owner, lost.expected)
    message = format_message(ErrorClass.PROTOCOL, PROTOCOL_MISMATCH, "the lease did not hold", registers, (read,))
    return Outcome(FAIL, message, {}, ErrorClass.PROTOCOL, PROTOCOL_MISMATCH, restore=False)


def defect_outcome(exc: Exception, where: str) -> Outcome:
    """A defect of the checker, not a PLC finding: it carries no error class, because none of the four is what was
    seen (§ Errors and debugging: never claim a cause not observed). The traceback is in the log."""
    message = (
        f"checker error: {type(exc).__name__}: {exc} (during {where}; the traceback is logged). Not a verdict on the "
        "PLC: repeat the run after the tool is fixed."
    )
    return Outcome(FAIL, message, {}, None, defect=True)


def exception_outcome(exc: PlcError | AckTimeout | LeaseHeld | LeaseLost, registers: RegisterMap) -> Outcome:
    """One cause, one class (protocol.md § Errors and debugging, rule 2)."""
    if isinstance(exc, LeaseLost):
        return lease_lost_outcome(exc, registers)
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
    except (PlcError, AckTimeout, LeaseHeld, LeaseLost) as exc:
        return exception_outcome(exc, ctx.registers)
    except Exception as exc:  # review #14: a checker defect ends in a report and exit 1, never a bare traceback
        log.exception("%s: checker defect", check.id)
        return defect_outcome(exc, check.id)
    finally:
        ctx.client.guard = None  # evidence, restore and cleanup must still reach the PLC


async def _restore(ctx: CheckContext) -> Outcome | None:
    if not ctx.client.connected:
        message = format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, "restore: not connected", ctx.registers)
        return Outcome(FAIL, message, {}, ErrorClass.TRANSPORT)
    try:
        why = await restore(ctx)
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return exception_outcome(exc, ctx.registers)
    return why
