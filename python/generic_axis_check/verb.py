"""One-verb mode (``--command <verb>``): send one verb to the axis and print every status read until it completes
(protocol.md § Conformance checks › Command line, "One-verb mode"; ADR-38).

Nothing here is a second implementation: pre-flight, the lease, the beat, the command handshake, the cleanup and the
error messages are the checklist's own (``runner``, ``context``, ``checks``, ``errors``).
"""

from __future__ import annotations

import asyncio
import contextlib
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

from .beat import Beater, LeaseLost
from .checks import (
    FAIL,
    HOME_TIMEOUT_S,
    PASS,
    UNITS,
    Outcome,
    ensure_enabled,
    fail,
    faulted,
    mismatch,
    motion_failed,
    passed,
    travel_timeout_s,
)
from .client import PlcClient, PlcError
from .context import STATE_TIMEOUT_S, AckTimeout, CheckContext, Options
from .dump import decode
from .errors import DRIVE_FAULT, HOME_LATCH_FAILED, ErrorClass, Read, format_message
from .lease import LeaseHeld
from .poll import ms, wait_for
from .registers import MAP_VERSION, AxisState, Command, RegisterMap, StatusBlock, register_ref
from .runner import cleanup, exception_outcome, preflight, to_completion

VERBS = ("enable", "disable", "home", "stop", "reset", "move", "jog")
"""protocol.md "One-verb mode": the verbs, in the Command line row's order."""

MOTION_VERBS = frozenset({"home", "move", "jog"})
"""Need ``--allow-motion``."""

VERBS_WITH_VALUE = frozenset({"move", "jog"})
"""``move <target>`` and ``jog <signed velocity>``, in axis units and axis units/s."""

DEFAULT_SPEED_PERCENT = 10.0
"""``--speed`` defaults to 10 % of ``MaxVelocity``."""

JOG_MOTION_S = 0.5
"""jog → ContinuousMotion observed within 500 ms after the ack."""

POLL_S = 0.02
"""Step 5: every status read at 20 ms."""

COMPLETED = "COMPLETED"
FAILED = "FAIL"
REFUSED = "REFUSED"
INTERRUPTED = "INTERRUPTED"
GUARD_REFUSED = "NOT SENT"


@dataclass(frozen=True, slots=True)
class Verb:
    name: str
    value: float | None = None
    """``move``: the target; ``jog``: the signed velocity (axis units, axis units/s)."""
    speed_percent: float = DEFAULT_SPEED_PERCENT
    run_for_s: float | None = None
    """``jog --for S``; None: until Ctrl-C."""

    def __str__(self) -> str:
        text = self.name if self.value is None else f"{self.name} {self.value:g}"
        if self.name == "move":
            text += f" --speed {self.speed_percent:g}"
        if self.run_for_s is not None:
            text += f" --for {self.run_for_s:g}"
        return text


@dataclass(slots=True)
class VerbResult:
    """The outcome of one verb (protocol.md "One-verb mode", exit codes)."""

    result: str
    exit_code: int
    message: str
    cleanup: list[str] = field(default_factory=list)


Out = Callable[[str], None]


def status_line(elapsed_ms: int, s: StatusBlock) -> str:
    """Step 5: ``State``, ``Flags``, ``ActualPosition``, ``ActualVelocity``, ``FaultCode``, ``CommandAck``."""
    return (
        f"{elapsed_ms:>7} ms  State {decode('State', s.state)} · Flags {decode('Flags', int(s.flags))} · "
        f"ActualPosition {s.actual_position / UNITS:.3f} · ActualVelocity {s.actual_velocity / UNITS:.3f} · "
        f"FaultCode {decode('FaultCode', s.fault_code)} · CommandAck {s.command_ack}"
    )


def map_problem(ctx: CheckContext, s: StatusBlock) -> Outcome | None:
    """Step 2: ``MapVersion ≠ 1`` or limits not sane → Protocol error, nothing written. All zero is "not published",
    which only ``move`` and ``jog`` refuse (step 3)."""
    if s.map_version != MAP_VERSION:
        return mismatch(ctx, "MapVersion not 1", Read("MapVersion", s.map_version, MAP_VERSION))
    if s.travel_min == s.travel_max == s.max_velocity == 0:
        return None
    if not (s.travel_min < s.travel_max and s.max_velocity > 0):
        reads = (Read("TravelMin", s.travel_min), Read("TravelMax", s.travel_max), Read("MaxVelocity", s.max_velocity))
        return mismatch(ctx, "limits not sane (TravelMin < TravelMax, MaxVelocity > 0)", *reads)
    return None


def raw(value: float) -> int:
    """Axis units → raw register units (0.001; protocol.md § Transport)."""
    return round(value * UNITS)


def guard_problem(verb: Verb, s: StatusBlock, registers: RegisterMap) -> str | None:
    """Step 3, before any write (Commander class, exit 2): the message names the register and its value."""

    def refuse(motion_error: str, what: str, *reads: Read) -> str:
        return format_message(ErrorClass.COMMANDER, motion_error, what, registers, reads)

    unpublished = s.travel_min == s.travel_max == s.max_velocity == 0
    limits = (Read("TravelMin", s.travel_min), Read("TravelMax", s.travel_max), Read("MaxVelocity", s.max_velocity))
    if verb.name == "move" and verb.value is not None:
        if unpublished:
            return refuse("OutOfRange", "move refused: the PLC publishes no limits", *limits)
        if not s.homed:
            return refuse("NotHomed", "move refused: the axis is not homed", Read("Flags", int(s.flags)))
        target = raw(verb.value)
        if not s.travel_min <= target <= s.travel_max:
            what = f"move refused: target {verb.value:g} is outside TravelMin..TravelMax"
            return refuse("OutOfRange", what, Read("TravelMin", s.travel_min), Read("TravelMax", s.travel_max))
    if verb.name == "jog" and verb.value is not None:
        if unpublished:
            return refuse("UnreachableSpeed", "jog refused: the PLC publishes no limits", *limits)
        if abs(raw(verb.value)) > s.max_velocity:
            what = f"jog refused: |{verb.value:g}| u/s is above MaxVelocity"
            return refuse("UnreachableSpeed", what, Read("MaxVelocity", s.max_velocity))
    return None


# ------------------------------------------------------------------------------------------------- the verbs


async def _until(
    ctx: CheckContext, predicate: Callable[[StatusBlock], bool], timeout_s: float, since: float
) -> tuple[StatusBlock, bool]:
    """Wait for ``predicate`` or ErrorStop; every read is printed by the client's status listener."""
    poll = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: predicate(s) or s.state == AxisState.ERROR_STOP,
        timeout_s,
        since=since,
    )
    return poll.status, poll.met and predicate(poll.status)


async def _enable(ctx: CheckContext, _verb: Verb) -> Outcome:
    ack = await ctx.command(Command.ENABLE)
    s, met = await _until(ctx, lambda s: s.state == AxisState.STANDSTILL, STATE_TIMEOUT_S, ack.written_at)
    if met:
        return passed(f"Standstill {ack.poll.elapsed_ms} ms after the ack")
    if s.state == AxisState.ERROR_STOP:
        return faulted(ctx, "Enable 1: ErrorStop instead of Standstill", s)
    what = f"no Standstill {STATE_TIMEOUT_S:g} s after Enable 1"
    return fail(ctx, ErrorClass.MACHINE, DRIVE_FAULT, what, Read("State", s.state, int(AxisState.STANDSTILL)))


async def _disable(ctx: CheckContext, _verb: Verb) -> Outcome:
    ack = await ctx.command(Command.NONE)
    s, met = await _until(ctx, lambda s: s.state == AxisState.DISABLED, STATE_TIMEOUT_S, ack.written_at)
    if met:
        return passed("Disabled")
    if s.state == AxisState.ERROR_STOP:
        return faulted(ctx, "Enable 0: ErrorStop instead of Disabled", s)
    what = f"no Disabled {STATE_TIMEOUT_S:g} s after Enable 0"
    return fail(ctx, ErrorClass.MACHINE, DRIVE_FAULT, what, Read("State", s.state, int(AxisState.DISABLED)))


async def _home(ctx: CheckContext, _verb: Verb) -> Outcome:
    not_ready = await ensure_enabled(ctx)  # "home … from Disabled set Enable first, as the driver's Home does"
    if not_ready:
        return not_ready
    ack = await ctx.command(Command.ENABLE | Command.HOME)
    s, met = await _until(ctx, lambda s: s.state == AxisState.STANDSTILL and s.homed, HOME_TIMEOUT_S, ack.written_at)
    if met:
        return passed("homed")
    if s.state == AxisState.ERROR_STOP:
        return faulted(ctx, "homing ended in ErrorStop", s)
    what = f"not homed {HOME_TIMEOUT_S:g} s after Home"
    return fail(ctx, ErrorClass.MACHINE, HOME_LATCH_FAILED, what, Read("State", s.state), Read("Flags", int(s.flags)))


async def _stop(ctx: CheckContext, _verb: Verb) -> Outcome:
    ack = await ctx.command(ctx.enabled | Command.STOP)
    at_rest = (AxisState.STANDSTILL, AxisState.DISABLED)
    s, met = await _until(ctx, lambda s: s.state in at_rest, STATE_TIMEOUT_S, ack.written_at)
    if met:
        return passed(f"State {decode('State', s.state)}")
    if s.state == AxisState.ERROR_STOP:
        return faulted(ctx, "Stop: ErrorStop instead of Standstill or Disabled", s)
    what = f"still moving {STATE_TIMEOUT_S:g} s after Stop"
    return motion_failed(ctx, what, Read("State", s.state), Read("ActualVelocity", s.actual_velocity))


async def _reset(ctx: CheckContext, _verb: Verb) -> Outcome:
    fault, _ = await ctx.watchdog()
    if fault:
        await ctx.clear_watchdog_fault()  # "reset writes WatchdogFault = 0 if set …"
    (word,) = await ctx.client.read(ctx.registers.command, 1)
    if word & Command.ENABLE:
        await ctx.command(Command.NONE)  # "… and Enable 0 before the Reset edge"
    ack = await ctx.command(Command.RESET)
    poll = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state != AxisState.ERROR_STOP, STATE_TIMEOUT_S, since=ack.written_at
    )
    if poll.met:
        return passed(f"State {decode('State', poll.status.state)}")
    return faulted(ctx, f"still in ErrorStop {STATE_TIMEOUT_S:g} s after Reset", poll.status)


async def _move(ctx: CheckContext, verb: Verb) -> Outcome:
    if verb.value is None:
        raise ValueError(f"{verb.name} needs a value")  # parse() guarantees it
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    target = raw(verb.value)
    velocity = max(1, round(start.max_velocity * verb.speed_percent / 100))
    await ctx.write_parameters(target, velocity, 0)  # parameters in one FC16, then the command FC16
    ack = await ctx.command(Command.ENABLE | Command.MOVE_ABSOLUTE)
    budget_s = travel_timeout_s(target - start.actual_position, velocity)
    s, met = await _until(ctx, lambda s: s.state == AxisState.STANDSTILL and s.in_position, budget_s, ack.written_at)
    if met:
        return passed(f"in position at {s.actual_position / UNITS:.3f}")
    if s.state == AxisState.ERROR_STOP:
        return faulted(ctx, "MoveAbsolute ended in ErrorStop", s)
    what = f"not arrived in position within {budget_s:.1f} s (2 × |target − start| ÷ velocity + {STATE_TIMEOUT_S:g} s)"
    reads = (
        Read("State", s.state, int(AxisState.STANDSTILL)),
        Read("Flags", int(s.flags)),
        Read("ActualPosition", s.actual_position, target),
    )
    return motion_failed(ctx, what, *reads)


async def _jog(ctx: CheckContext, verb: Verb) -> Outcome:
    if verb.value is None:
        raise ValueError(f"{verb.name} needs a value")  # parse() guarantees it
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    await ctx.write_parameters(start.actual_position, raw(verb.value), 0)
    await ctx.command(Command.ENABLE | Command.MOVE_VELOCITY)
    acked_at = time.monotonic()
    s, met = await _until(ctx, lambda s: s.state == AxisState.CONTINUOUS_MOTION, JOG_MOTION_S, acked_at)
    if not met:
        if s.state == AxisState.ERROR_STOP:
            return faulted(ctx, "MoveVelocity: ErrorStop instead of ContinuousMotion", s)
        what = f"no ContinuousMotion {ms(JOG_MOTION_S)} ms after the MoveVelocity ack"
        return motion_failed(ctx, what, Read("State", s.state, int(AxisState.CONTINUOUS_MOTION)))
    # The verb has completed: ContinuousMotion observed. Print until --for elapses or Ctrl-C, then Stop.
    ends = None if verb.run_for_s is None else acked_at + verb.run_for_s
    try:
        while ends is None or time.monotonic() < ends:
            s = await ctx.status()
            if s.state == AxisState.ERROR_STOP:
                return faulted(ctx, "the jog ended in ErrorStop", s)
            await asyncio.sleep(POLL_S)
    except asyncio.CancelledError:
        task = asyncio.current_task()
        if task is not None:
            task.uncancel()  # Ctrl-C after ContinuousMotion was observed ends the jog: Stop, and exit 0
    stopped: list[Outcome] = []

    async def stop() -> None:
        stopped.append(await _stop(ctx, verb))

    await to_completion(stop())
    return (
        stopped[0]
        if stopped[0].result == FAIL
        else passed(f"ContinuousMotion observed; after the jog, Stop: {stopped[0].message}")
    )


RUN: dict[str, Callable[[CheckContext, Verb], Awaitable[Outcome]]] = {
    "enable": _enable,
    "disable": _disable,
    "home": _home,
    "stop": _stop,
    "reset": _reset,
    "move": _move,
    "jog": _jog,
}


# ------------------------------------------------------------------------------------------------- the run


async def run_verb(options: Options, verb: Verb, out: Out) -> VerbResult:
    """protocol.md "One-verb mode" steps 1–6. Never raises for a PLC problem; exit 0/1/2/3/4 per the protocol."""
    registers = RegisterMap(options.command_base, options.status_base)
    client = PlcClient(options.host, options.port, options.unit, registers)
    ctx = CheckContext(client, registers, options, Beater(client, registers))
    ctx.beater.expected_owner = lambda: options.owner_id if ctx.holds_lease else None
    started = time.monotonic()
    proven_free = False
    result = VerbResult(INTERRUPTED, 4, f"interrupted by the operator before {verb.name} completed")

    def finish(outcome: Outcome) -> VerbResult:
        code = 0 if outcome.result == PASS else 1
        return VerbResult(COMPLETED if code == 0 else FAILED, code, f"{verb.name}: {outcome.message}")

    try:
        try:
            await client.connect()
            verdict = await preflight(client, registers)  # step 1, writes nothing
        except PlcError as exc:
            return finish(exception_outcome(exc, registers))
        if verdict.refusal is not None:
            return VerbResult(REFUSED, 3, f"Pre-flight: {verdict.refusal}")
        proven_free = True
        if verdict.note is not None:
            out(f"Pre-flight: {verdict.note}")
        status = await ctx.status()  # step 2
        problem = map_problem(ctx, status)
        if problem is not None:
            return finish(problem)
        refusal = guard_problem(verb, status, registers)  # step 3: before any write, the lease included
        if refusal is not None:
            return VerbResult(GUARD_REFUSED, 2, f"{verb.name}: {refusal}")
        (word,) = await client.read(registers.command, 1)
        ctx.found_enabled = bool(word & Command.ENABLE)
        ctx.command_word = word & int(Command.ENABLE)  # a Stop or Reset keeps (or drops) the Enable it found
        await ctx.take_lease()
        await ctx.beater.start()
        out(f"lease taken: LeaseOwner ({register_ref(registers, 'LeaseOwner')}) = {options.owner_id}; beating")
        if verdict.foreign_trip:
            # Step 1: "proceed as the driver does at attach": the dead holder's trip is cleared; ErrorStop and
            # FaultCode 4 stay for `reset`.
            await ctx.clear_watchdog_fault()
            out(f"WatchdogFault ({register_ref(registers, 'WatchdogFault')}) = 0 written (the dead holder's trip)")
        client.status_listener = lambda s: out(status_line(ms(time.monotonic() - started), s))
        client.guard = ctx.beater.raise_if_failed  # a dead beat is Transport, never a Machine trip (review #7)
        try:
            outcome = await RUN[verb.name](ctx, verb)  # steps 4 and 5
        except (PlcError, AckTimeout, LeaseHeld, LeaseLost) as exc:
            outcome = exception_outcome(exc, registers)
        result = finish(outcome)
    except asyncio.CancelledError:
        task = asyncio.current_task()
        if task is not None:
            task.uncancel()
    except (PlcError, LeaseHeld) as exc:  # the attach itself (status read, lease, beat) failed
        result = finish(exception_outcome(exc, registers))
    finally:
        client.guard = None
        client.status_listener = None
        if client.connected and proven_free:
            await to_completion(cleanup(ctx))  # step 6
        with contextlib.suppress(PlcError):
            await ctx.beater.stop()
        client.close()
    result.cleanup = ctx.cleanup_log
    return result
