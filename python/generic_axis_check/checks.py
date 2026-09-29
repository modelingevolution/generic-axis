"""``CHECKS``: CHK-01…CHK-16 of protocol.md § Conformance checks, in id order, one function per check.

Every threshold below cites the table row it comes from. Positions and velocities are raw register values
(0.001 axis unit); the report carries them raw (protocol.md § Report schema: "Numbers are raw integers or durations").
"""

from __future__ import annotations

import asyncio
import contextlib
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field

from .beat import BEAT_PERIOD_S, Beater
from .client import PlcError
from .context import (
    ACK_TIMEOUT_S,
    FOREIGN_OWNER_ID,
    LEASE_TIMEOUT_S,
    STATE_TIMEOUT_S,
    STOP_HALT_S,
    CheckContext,
)
from .errors import (
    DRIVE_FAULT,
    HOME_LATCH_FAILED,
    MOTION_FAILED,
    PROTOCOL_MISMATCH,
    WATCHDOG_TRIPPED,
    ErrorClass,
    Read,
    format_message,
    machine_error,
)
from .lease import LeaseHeld, LeaseTaken, acquire
from .poll import POLL_PERIOD_S, ms, wait_for
from .registers import (
    MAP_VERSION,
    VALID_STATES,
    AxisState,
    Command,
    FaultCode,
    StatusBlock,
    from_words,
)

PASS = "PASS"
FAIL = "FAIL"
SKIPPED = "SKIPPED"

UNITS = 1000
"""protocol.md § Transport: positions and velocities are 0.001 unit; "raw register values ÷ 1000"."""

CADENCE_READS = 30
CADENCE_PERIOD_S = 0.1
CADENCE_MAX_ROUND_TRIP_MS = 100
"""CHK-04: 30 status-block reads, one every 100 ms; the slowest round trip is ≤ 100 ms."""

WORD_ORDER_VALUES = ((65538, (0x0002, 0x0001)), (-2, (0xFFFE, 0xFFFF)))
"""CHK-05: 65 538 as ``[0x0002, 0x0001]`` and −2 as ``[0xFFFE, 0xFFFF]`` (low word first)."""

OWNERSHIP_WAIT_S = 1.0
"""CHK-05: "Wait 1 s and read again"."""

ARMED_BEAT_S = 2.0
"""CHK-08, CHK-09, CHK-10: beat for 2 s before the stall or release."""

TRIP_WINDOW_MS = (1000, 1500)
"""CHK-08, CHK-09, CHK-16: the trip lands within 1.0–1.5 s of the last beat (ADR-31)."""

LATCHED_BEAT_S = 1.0
"""CHK-09: "Without clearing, beat 1 s: no second trip is counted"."""

RELEASE_WAIT_S = 2.0
"""CHK-10: after ``LeaseOwner = 0`` and the last beat, wait 2 s."""

LEASE_TAKEOVER_MS = 2000
"""CHK-11 (c): taken within 2 s of the incumbent's last beat."""

WATCH_BEFORE_STALL_S = 3 * BEAT_PERIOD_S
"""CHK-11 (c): "Stop the incumbent's beat while the client watches" — the client sees three beats change first."""

INCUMBENT_TRIP_SETTLE_S = 1.6
"""CHK-11: before restoring after (c), wait until ``WatchdogFault`` reads 1 or 1.6 s have passed since the incumbent's
last beat (the latest FR-11 trip, 1.5 s, plus one 100 ms read)."""

HOME_TIMEOUT_S = 120.0
"""CHK-12: ``State == 1`` with ``Homed`` within 120 s."""

MOVE_OFFSET = 10 * UNITS
"""CHK-13: target ``TravelMin + 10`` axis units."""

DISCRETE_SPEED_PERCENT = 10
"""CHK-13, CHK-14: velocity 10 % of ``MaxVelocity``."""

JOG_SPEED_PERCENT = 1
"""CHK-15, CHK-16: MoveVelocity at +1 % of ``MaxVelocity`` (away from ``TravelMin``)."""

CRUISE_FRACTION = 0.9
CRUISE_WAIT_S = 2.0
"""CHK-14: write Stop once ``abs(ActualVelocity)`` ≥ 90 % of the commanded speed, or after 2 s."""

MOTION_START_S = 2.0
"""CHK-16: ``ActualVelocity > 0`` within 2 s of the ack, then stop beating (protocol.md, lead ruling on #10)."""

JOG_RUN_S = 1.0
"""CHK-15: MoveVelocity for 1 s, then Stop."""


def travel_timeout_s(distance: int, velocity: int) -> float:
    """CHK-13: arrives within 2 × |target − start| ÷ velocity + 5 s (protocol.md, lead ruling on #10)."""
    return 2 * abs(distance) / max(velocity, 1) + STATE_TIMEOUT_S


@dataclass(slots=True)
class Outcome:
    result: str
    message: str
    observed: dict[str, int | None] = field(default_factory=dict)
    error_class: ErrorClass | None = None
    """Set on a FAIL (protocol.md § Report schema, ``errorClass``)."""
    motion_error: str | None = None
    """The SDK ``MotionError`` name of a FAIL (the runner re-checks a ``WatchdogTripped`` against the beat, #7)."""
    restore: bool = True
    """False when the check wrote nothing and found the axis unfit: the runner then writes nothing either (#9)."""
    defect: bool = False
    """The checker itself failed (an unexpected exception): the run stops after cleanup (review #14)."""


def passed(message: str, **observed: int | None) -> Outcome:
    return Outcome(PASS, message, dict(observed))


def fail(
    ctx: CheckContext,
    error_class: ErrorClass,
    motion_error: str,
    what: str,
    *reads: Read,
    **observed: int | None,
) -> Outcome:
    """A FAIL whose message has the protocol's shape (§ Errors and debugging, rule 1)."""
    message = format_message(error_class, motion_error, what, ctx.registers, reads)
    return Outcome(FAIL, message, dict(observed), error_class, motion_error)


def mismatch(ctx: CheckContext, what: str, *reads: Read, **observed: int | None) -> Outcome:
    """Protocol/ProtocolMismatch: the PLC answered, but not per protocol.md."""
    return fail(ctx, ErrorClass.PROTOCOL, PROTOCOL_MISMATCH, what, *reads, **observed)


def motion_failed(ctx: CheckContext, what: str, *reads: Read, **observed: int | None) -> Outcome:
    """Machine/MotionFailed: an accepted command the machine did not carry out within the budget."""
    return fail(ctx, ErrorClass.MACHINE, MOTION_FAILED, what, *reads, **observed)


def faulted(ctx: CheckContext, what: str, status: StatusBlock, **observed: int | None) -> Outcome:
    """The PLC reports ErrorStop: the class and MotionError follow ``FaultCode`` (a 0 code is a mismatch)."""
    error_class, motion_error = machine_error(status)
    reads = (Read("State", status.state), Read("FaultCode", status.fault_code))
    return fail(ctx, error_class, motion_error, what, *reads, **observed)


Run = Callable[[CheckContext], Awaitable[Outcome]]

OBSERVED: dict[str, tuple[str, ...]] = {
    # protocol.md § Observed values: exactly these keys, in this order; the runner appends ``retries``.
    "CHK-01": ("connectMs", "readMs"),
    "CHK-02": ("mapVersion",),
    "CHK-03": ("travelMin", "travelMax", "maxVelocity"),
    "CHK-04": ("reads", "slowestMs", "invalidStates"),
    "CHK-05": ("firstReadBack", "secondReadBack", "secondReadBackAfter1s"),
    "CHK-06": ("enableAckMs", "enableStateMs", "disableAckMs", "disableStateMs"),
    "CHK-07": ("ackMs", "state", "faultCode"),
    "CHK-08": ("tripAfterMs", "watchdogTrips", "faultCode", "state"),
    "CHK-09": ("setupTripAfterMs", "tripsWhileLatched", "tripsWhileBeating", "secondTripAfterMs", "watchdogTrips"),
    "CHK-10": ("tripsAfterRelease", "watchdogFault"),
    "CHK-11": ("ownIdReadBack", "refusedAfterMs", "leaseOwnerAfterRefusal", "takenAfterMs"),
    "CHK-12": ("ackMs", "homedAfterMs", "faultCode"),
    "CHK-13": ("target", "ackMs", "arrivedAfterMs", "position", "positionError"),
    "CHK-14": ("commandedVelocity", "velocityAtStop", "ackMs", "haltMs"),
    "CHK-15": ("commandedVelocity", "ackMs", "maxVelocitySeen", "stopAckMs", "haltMs"),
    "CHK-16": ("commandedVelocity", "tripAfterMs", "haltAfterTripMs", "homedAfterTrip"),
}
RETRIES_KEY = "retries"


@dataclass(frozen=True, slots=True)
class Check:
    id: str
    title: str
    section: str
    needs: tuple[str, ...]
    motion: bool
    run: Run


# --------------------------------------------------------------------------------------------------- helpers


@dataclass(slots=True)
class TripWatch:
    met: bool
    after_ms: int
    """From the last beat to the first read that showed the whole trip (or to the last read)."""
    before_ms: int
    """From the last beat to the last read that did not show it (0: the last beat itself). The trip happened in
    ``before_ms``…``after_ms`` (protocol.md § Rules, "Timing": bounds are judged at the read cadence, #31)."""
    status: StatusBlock
    watchdog_fault: int
    trips: int
    stamp: float


async def watch_trip(ctx: CheckContext, last_beat: float, trips_before: int, *, need_latch: bool = True) -> TripWatch:
    """Poll every 20 ms until the FR-11 trip is visible or 1.5 s pass after ``last_beat``.

    A trip is ``State == 7`` and ``FaultCode == 4``, plus, with ``need_latch``, ``WatchdogFault == 1`` and
    ``WatchdogTrips`` + 1 (CHK-08/09). CHK-16 asks for ``FaultCode 4`` and ``State 7`` only.
    """
    deadline = last_beat + TRIP_WINDOW_MS[1] / 1000
    before_ms = 0
    while True:
        status = await ctx.status()
        fault, trips = await ctx.watchdog()
        stamp = time.monotonic()
        tripped = status.state == AxisState.ERROR_STOP and status.fault_code == FaultCode.WATCHDOG
        if need_latch:
            tripped = tripped and fault == 1 and trips == (trips_before + 1) & 0xFFFF
        if tripped or fault == 1:
            ctx.caused_trip = True
        if tripped or stamp >= deadline:
            return TripWatch(tripped, ms(stamp - last_beat), before_ms, status, fault, trips, stamp)
        before_ms = ms(stamp - last_beat)
        await asyncio.sleep(POLL_PERIOD_S)


def judge_trip(ctx: CheckContext, watch: TripWatch, label: str, **observed: int | None) -> Outcome | None:
    """A Protocol mismatch for a trip outside the 1.0–1.5 s window (FR-11), or None when it is inside."""
    reads = (
        Read("State", watch.status.state),
        Read("FaultCode", watch.status.fault_code),
        Read("WatchdogFault", watch.watchdog_fault),
        Read("WatchdogTrips", watch.trips),
    )
    if not watch.met:
        what = (
            f"{label}: no trip within {TRIP_WINDOW_MS[1] / 1000:g} s of the last beat (last read {watch.after_ms} ms)"
        )
        return mismatch(ctx, what, *reads, **observed)
    # Review #31, protocol.md § Rules "Timing": the trip happened between the last read without it and the first read
    # with it; it is early only if that first read is before 1.0 s, late only if that last read is already after 1.5 s.
    if watch.after_ms < TRIP_WINDOW_MS[0]:
        what = f"{label}: tripped {trip_interval(watch)}, before the 1 s stall window"
        return mismatch(ctx, what, *reads, **observed)
    if watch.before_ms > TRIP_WINDOW_MS[1]:
        what = f"{label}: tripped {trip_interval(watch)}, after the 1.5 s bound"
        return mismatch(ctx, what, *reads, **observed)
    return None


def trip_interval(watch: TripWatch) -> str:
    return f"between {watch.before_ms} and {watch.after_ms} ms after the last beat"


async def beat_for(ctx: CheckContext, seconds: float) -> None:
    """Beat for ``seconds`` from now (starting the beat if needed): the procedure's step ("WatchdogFault = 0, beat
    for 2 s"), not the beat's age — a beat already running since restore does not shorten the step (review #13)."""
    await ctx.beater.start()
    await asyncio.sleep(seconds)
    ctx.beater.raise_if_failed()


async def ensure_enabled(ctx: CheckContext) -> Outcome | None:
    """Enable and wait for Standstill (CHK-06's 5 s budget). Returns a failure, or None when the axis is ready."""
    status = await ctx.status()
    if status.state == AxisState.STANDSTILL and ctx.enabled:
        return None
    ack = await ctx.command(Command.ENABLE)
    poll = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state in (AxisState.STANDSTILL, AxisState.ERROR_STOP),
        STATE_TIMEOUT_S,
        since=ack.written_at,
    )
    if poll.status.state == AxisState.ERROR_STOP:
        return faulted(ctx, "Enable 1: ErrorStop instead of Standstill", poll.status)
    if not poll.met:
        what = f"no Standstill {STATE_TIMEOUT_S:g} s after Enable 1"
        read = Read("State", poll.status.state, int(AxisState.STANDSTILL))
        return fail(ctx, ErrorClass.MACHINE, DRIVE_FAULT, what, read)
    return None


def percent_of(value: int, percent: int) -> int:
    return value * percent // 100


# --------------------------------------------------------------------------------------------------- checks


async def chk01(ctx: CheckContext) -> Outcome:
    """The run connected before pre-flight (a failed connect FAILs CHK-01 there); this times one status read."""
    started = time.monotonic()
    await ctx.status()
    read_ms = ms(time.monotonic() - started)
    return passed(
        f"connected to {ctx.client.host}:{ctx.client.port}, unit {ctx.client.unit} answers",
        connectMs=ctx.connect_ms,
        readMs=read_ms,
    )


async def chk02(ctx: CheckContext) -> Outcome:
    (version,) = await ctx.client.read(ctx.registers.map_version, 1)
    if version != MAP_VERSION:
        return mismatch(ctx, "MapVersion not 1", Read("MapVersion", version, MAP_VERSION), mapVersion=version)
    return passed(f"MapVersion {version}", mapVersion=version)


async def chk03(ctx: CheckContext) -> Outcome:
    s = await ctx.status()
    observed = {"travelMin": s.travel_min, "travelMax": s.travel_max, "maxVelocity": s.max_velocity}
    values = f"TravelMin {s.travel_min}, TravelMax {s.travel_max}, MaxVelocity {s.max_velocity}"
    reads = (Read("TravelMin", s.travel_min), Read("TravelMax", s.travel_max), Read("MaxVelocity", s.max_velocity))
    if s.travel_min == 0 and s.travel_max == 0 and s.max_velocity == 0:
        return mismatch(ctx, "limits not published (all zero)", *reads, **observed)
    problems = []
    if not s.travel_min < s.travel_max:
        problems.append("TravelMin is not < TravelMax")
    if not s.max_velocity > 0:
        problems.append("MaxVelocity is not > 0")
    if problems:
        return mismatch(ctx, "limits not sane: " + "; ".join(problems), *reads, **observed)
    return passed(values, **observed)


async def chk04(ctx: CheckContext) -> Outcome:
    slowest = 0
    bad_states: list[int] = []
    next_at = time.monotonic()
    for _ in range(CADENCE_READS):
        started = time.monotonic()
        status = await ctx.status()
        slowest = max(slowest, ms(time.monotonic() - started))
        if status.state not in VALID_STATES:
            bad_states.append(status.state)
        next_at += CADENCE_PERIOD_S
        await asyncio.sleep(max(0.0, next_at - time.monotonic()))
    observed = {"reads": CADENCE_READS, "slowestMs": slowest, "invalidStates": len(bad_states)}
    if bad_states:
        what = f"State outside {{0,1,2,3,4,6,7}} in {len(bad_states)} of {CADENCE_READS} reads"
        return mismatch(ctx, what, Read("State", bad_states[0]), **observed)
    if slowest > CADENCE_MAX_ROUND_TRIP_MS:
        what = f"status block mirrored too slowly: slowest round trip {slowest} ms > {CADENCE_MAX_ROUND_TRIP_MS} ms"
        return mismatch(ctx, what, **observed)
    return passed(f"{CADENCE_READS} reads, slowest {slowest} ms", **observed)


async def chk05(ctx: CheckContext) -> Outcome:
    address = ctx.registers.target_position
    observed: dict[str, int | None] = {}
    for index, (value, words) in enumerate(WORD_ORDER_VALUES, start=1):
        await ctx.client.write(address, list(words))
        back = await ctx.client.read(address, 2)
        observed[("firstReadBack", "secondReadBack")[index - 1]] = from_words(*back)
        if tuple(back) != words:
            what = f"wrote {value} as {list(map(hex, words))} to C+2…C+3, read back {list(map(hex, back))}"
            return mismatch(ctx, what, Read("TargetPosition", from_words(*back), value), **observed)
    await asyncio.sleep(OWNERSHIP_WAIT_S)
    back = await ctx.client.read(address, 2)
    observed["secondReadBackAfter1s"] = from_words(*back)
    last_words = WORD_ORDER_VALUES[-1][1]
    if tuple(back) != last_words:
        what = f"the PLC changed a driver-owned register within {OWNERSHIP_WAIT_S:g} s"
        return mismatch(ctx, what, Read("TargetPosition", from_words(*back), WORD_ORDER_VALUES[-1][0]), **observed)
    return passed("65538 and -2 read back exactly and stayed", **observed)


async def chk06(ctx: CheckContext) -> Outcome:
    # "Precondition State 0 or 1", checked before the first write. No silent recovery (rule 3, review #9): an axis
    # found faulted or moving FAILs with what was read, and nothing is written to it (no restore after this FAIL).
    status = await ctx.status()
    if status.state not in (AxisState.DISABLED, AxisState.STANDSTILL):
        if status.state == AxisState.ERROR_STOP:
            outcome = faulted(ctx, "precondition: State 0 or 1 expected; reset the axis first", status)
        else:
            what = "precondition: the axis is not at rest"
            outcome = motion_failed(ctx, what, Read("State", status.state), Read("FaultCode", status.fault_code))
        outcome.restore = False
        return outcome
    fault, _ = await ctx.watchdog()
    if fault and ctx.foreign_trip:
        # protocol.md "Pre-flight", dead commander: its trip is left for its operator. FR-11 "At attach" would clear
        # it; the checker does not (review #35).
        what = "the previous commander's watchdog trip is left for its operator; the checker does not clear it"
        outcome = fail(ctx, ErrorClass.MACHINE, WATCHDOG_TRIPPED, what, Read("WatchdogFault", fault))
        outcome.restore = False
        return outcome
    await ctx.take_lease()
    ctx.session_lease = True
    fault, _ = await ctx.watchdog()
    if fault:
        await ctx.clear_watchdog_fault()  # FR-11 "At attach": a latched trip belongs to a dead predecessor
    await ctx.beater.start()

    on = await ctx.command(Command.ENABLE)
    on_state = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state == AxisState.STANDSTILL, STATE_TIMEOUT_S, since=on.written_at
    )
    observed = {"enableAckMs": on.poll.elapsed_ms, "enableStateMs": on_state.elapsed_ms}
    if not on_state.met:
        what = f"no Standstill {STATE_TIMEOUT_S:g} s after Enable 1"
        read = Read("State", on_state.status.state, int(AxisState.STANDSTILL))
        return fail(ctx, ErrorClass.MACHINE, DRIVE_FAULT, what, read, **observed)
    off = await ctx.command(Command.NONE)
    off_state = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state == AxisState.DISABLED, STATE_TIMEOUT_S, since=off.written_at
    )
    observed |= {"disableAckMs": off.poll.elapsed_ms, "disableStateMs": off_state.elapsed_ms}
    if not off_state.met:
        what = f"not Disabled {STATE_TIMEOUT_S:g} s after Enable 0"
        read = Read("State", off_state.status.state, int(AxisState.DISABLED))
        return fail(ctx, ErrorClass.MACHINE, DRIVE_FAULT, what, read, **observed)
    return passed(
        f"Enable ack {on.poll.elapsed_ms} ms, Standstill {on_state.elapsed_ms} ms; "
        f"disable ack {off.poll.elapsed_ms} ms, Disabled {off_state.elapsed_ms} ms",
        **observed,
    )


async def chk07(ctx: CheckContext) -> Outcome:
    status = await ctx.status()
    if status.state != AxisState.DISABLED or status.fault_code != FaultCode.NONE:
        what = "precondition: State 0 with FaultCode 0 expected"
        return faulted(ctx, what, status, state=status.state, faultCode=status.fault_code)
    ack = await ctx.command(Command.RESET)
    # "State stays 0 and FaultCode stays 0": watched for one ack window after the ack.
    changed = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state != AxisState.DISABLED or s.fault_code != FaultCode.NONE,
        ACK_TIMEOUT_S,
    )
    observed = {"ackMs": ack.poll.elapsed_ms, "state": changed.status.state, "faultCode": changed.status.fault_code}
    if changed.met or ack.poll.status.state != AxisState.DISABLED:
        return mismatch(
            ctx,
            "Reset outside ErrorStop changed the axis",
            Read("State", changed.status.state, int(AxisState.DISABLED)),
            Read("FaultCode", changed.status.fault_code, int(FaultCode.NONE)),
            **observed,
        )
    return passed(f"ack {ack.poll.elapsed_ms} ms, no-op", **observed)


async def chk08(ctx: CheckContext) -> Outcome:
    await ctx.clear_watchdog_fault()
    _, trips_before = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    last_beat = await ctx.beater.stop_beating()
    watch = await watch_trip(ctx, last_beat, trips_before)
    observed = {
        "tripAfterMs": watch.after_ms,
        "watchdogTrips": watch.trips,
        "faultCode": watch.status.fault_code,
        "state": watch.status.state,
    }
    problem = judge_trip(ctx, watch, "stalled beat", **observed)
    if problem:
        return problem
    return passed(f"tripped {trip_interval(watch)}", **observed)


async def chk09(ctx: CheckContext) -> Outcome:
    # Setup: first trip the watchdog as in CHK-08 (protocol.md CHK-09; no trip → FAIL "setup: no trip").
    await ctx.clear_watchdog_fault()
    _, trips0 = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    first = await watch_trip(ctx, await ctx.beater.stop_beating(), trips0)
    observed = {"setupTripAfterMs": first.after_ms}
    problem = judge_trip(ctx, first, "setup", **observed)
    if problem:
        return problem
    trips1 = first.trips

    # Latched: beat without clearing, then stall again; the network must stay disarmed.
    await ctx.beater.start()
    await asyncio.sleep(LATCHED_BEAT_S)
    await ctx.beater.stop()
    await asyncio.sleep(TRIP_WINDOW_MS[1] / 1000)
    _, trips_latched = await ctx.watchdog()
    observed["tripsWhileLatched"] = (trips_latched - trips1) & 0xFFFF
    if trips_latched != trips1:
        what = "a trip was counted while WatchdogFault was latched"
        return mismatch(ctx, what, Read("WatchdogTrips", trips_latched, trips1), **observed)

    # Re-arm: Reset edge, WatchdogFault = 0, beat 2 s (no trip), stall → second trip.
    await ctx.command(Command.RESET)
    await ctx.clear_watchdog_fault()
    ctx.caused_trip = False
    await beat_for(ctx, ARMED_BEAT_S)
    fault, trips_beating = await ctx.watchdog()
    observed["tripsWhileBeating"] = (trips_beating - trips1) & 0xFFFF
    if fault or trips_beating != trips1:
        reads = (Read("WatchdogFault", fault, 0), Read("WatchdogTrips", trips_beating, trips1))
        return mismatch(ctx, "tripped while beating after the clear", *reads, **observed)
    second = await watch_trip(ctx, await ctx.beater.stop_beating(), trips1)
    observed |= {"secondTripAfterMs": second.after_ms, "watchdogTrips": second.trips}
    problem = judge_trip(ctx, second, "after clear", **observed)
    if problem:
        return problem
    return passed(f"disarmed while latched; re-armed on clear, second trip {trip_interval(second)}", **observed)


async def chk10(ctx: CheckContext) -> Outcome:
    await ctx.clear_watchdog_fault()
    _, trips_before = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    ctx.holds_lease = False  # before the write: the beat checks LeaseOwner while it holds the lease
    await ctx.client.write(ctx.registers.lease_owner, [0])
    await ctx.beater.stop()
    await asyncio.sleep(RELEASE_WAIT_S)
    fault, trips = await ctx.watchdog()
    observed = {"watchdogFault": fault, "tripsAfterRelease": (trips - trips_before) & 0xFFFF}
    if fault or trips != trips_before:
        reads = (Read("WatchdogFault", fault, 0), Read("WatchdogTrips", trips, trips_before))
        return mismatch(ctx, "tripped after a clean release (LeaseOwner = 0)", *reads, **observed)
    return passed(f"no trip {RELEASE_WAIT_S:g} s after LeaseOwner = 0", **observed)


async def chk11(ctx: CheckContext) -> Outcome:
    registers, own = ctx.registers, ctx.options.owner_id
    if ctx.holds_lease:
        await ctx.release_lease()
    observed: dict[str, int | None] = {}

    # (a) unowned → taken, read back, released.
    await ctx.client.write(registers.lease_owner, [0])
    await acquire(ctx.client, registers, own, LEASE_TIMEOUT_S)
    (read_back,) = await ctx.client.read(registers.lease_owner, 1)
    observed["ownIdReadBack"] = read_back
    await ctx.client.write(registers.lease_owner, [0])
    if read_back != own:
        return mismatch(ctx, "(a) the lease did not read back", Read("LeaseOwner", read_back, own), **observed)

    incumbent = Beater(ctx.client, registers)
    take: asyncio.Task[LeaseTaken] | None = None
    try:
        # (b) a live foreign incumbent → refused after the 3 s timeout, its lease untouched.
        await ctx.client.write(registers.lease_owner, [FOREIGN_OWNER_ID])
        await incumbent.start()
        started = time.monotonic()
        try:
            await acquire(ctx.client, registers, own, LEASE_TIMEOUT_S)
        except LeaseHeld as held:
            observed["refusedAfterMs"] = ms(time.monotonic() - started)
            refused_owner = held.owner
        else:
            ctx.holds_lease = True
            what = f"(b) the lease client saw no beat from owner {FOREIGN_OWNER_ID} and took the lease"
            return mismatch(ctx, what, **observed)
        (owner_b,) = await ctx.client.read(registers.lease_owner, 1)
        observed["leaseOwnerAfterRefusal"] = owner_b
        if refused_owner != FOREIGN_OWNER_ID or owner_b != FOREIGN_OWNER_ID:
            what = f"(b) the refusal named owner {refused_owner}"
            return mismatch(ctx, what, Read("LeaseOwner", owner_b, FOREIGN_OWNER_ID), **observed)

        # (c) the incumbent dies while the client watches → taken within 2 s of its last beat.
        take = asyncio.create_task(acquire(ctx.client, registers, own, LEASE_TIMEOUT_S))
        await asyncio.sleep(WATCH_BEFORE_STALL_S)
        last_beat = await incumbent.stop_beating()
        try:
            taken = await take
        except LeaseHeld as held:
            what = f"(c) not taken {LEASE_TIMEOUT_S:g} s after the incumbent stopped beating"
            what += ": Heartbeat kept changing after the incumbent stopped writing it, or LeaseOwner did not hold"
            return mismatch(ctx, what, Read("LeaseOwner", held.owner, own), **observed)
        ctx.holds_lease = True
        taken_ms = ms(taken.taken_at - last_beat)
        observed["takenAfterMs"] = taken_ms
    finally:
        if take is not None and not take.done():
            # Review #12: a Ctrl-C during (c) must not leave the lease client running into cleanup, where it would
            # write LeaseOwner = the checker's id after cleanup released the lease.
            take.cancel()
            with contextlib.suppress(asyncio.CancelledError, LeaseHeld, PlcError):
                await take
        await incumbent.stop()
        # "Any trip caused by (c) is cleaned up": the incumbent's stall trips the PLC watchdog 1.0–1.5 s after its
        # last beat, which can be AFTER the lease was taken. Wait for it (WatchdogFault 1) or 1.6 s from that beat,
        # else a restore reading 0 is followed by the trip landing (review #49, C# 2c35bfb).
        fault, _ = await ctx.watchdog()
        if incumbent.last_beat is not None:
            settle_until = incumbent.last_beat + INCUMBENT_TRIP_SETTLE_S
            while fault == 0 and time.monotonic() < settle_until:
                await asyncio.sleep(POLL_PERIOD_S)
                fault, _ = await ctx.watchdog()
        if fault:
            ctx.caused_trip = True
            await ctx.keep_evidence()  # a FAIL of (b)/(c) cites the axis before this recovery wrote to it
            await ctx.recover()
        if not ctx.session_lease and ctx.holds_lease:
            await ctx.release_lease()

    if taken_ms > LEASE_TAKEOVER_MS:
        what = f"(c) taken {taken_ms} ms after the incumbent's last beat, > {LEASE_TAKEOVER_MS} ms"
        return mismatch(ctx, what, **observed)
    return passed(
        f"(a) {read_back}; (b) refused, LeaseHeld {FOREIGN_OWNER_ID}; (c) taken after "
        f"{observed['takenAfterMs']} ms",
        **observed,
    )


async def chk12(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    ack = await ctx.command(Command.ENABLE | Command.HOME)
    shown = ack.poll.status
    if shown.state != AxisState.HOMING and not (shown.state == AxisState.STANDSTILL and shown.homed):
        # "Ack in the scan that enters the state": Homing, or already done in that scan (Standstill with Homed).
        what = "the Home ack did not show its state (ack in the scan that enters it)"
        return mismatch(ctx, what, Read("State", shown.state, int(AxisState.HOMING)), ackMs=ack.poll.elapsed_ms)
    done = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state == AxisState.ERROR_STOP or (s.state == AxisState.STANDSTILL and s.homed),
        HOME_TIMEOUT_S,
        since=ack.written_at,
    )
    s = done.status
    observed = {
        "ackMs": ack.poll.elapsed_ms,
        "homedAfterMs": done.elapsed_ms,
        "faultCode": s.fault_code,
    }
    if not done.met:
        what = f"not homed {HOME_TIMEOUT_S:g} s after Home"
        reads = (Read("State", s.state), Read("Flags", int(s.flags)))
        return fail(ctx, ErrorClass.MACHINE, HOME_LATCH_FAILED, what, *reads, **observed)
    if s.state != AxisState.STANDSTILL or s.fault_code != FaultCode.NONE:
        return faulted(ctx, "homing ended in ErrorStop", s, **observed)
    return passed(f"homed in {done.elapsed_ms / 1000:.2f} s", **observed)


async def chk13(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    target = start.travel_min + MOVE_OFFSET
    velocity = percent_of(start.max_velocity, DISCRETE_SPEED_PERCENT)
    await ctx.write_parameters(target, velocity, 0)
    ack = await ctx.command(Command.ENABLE | Command.MOVE_ABSOLUTE)
    observed = {"target": target, "ackMs": ack.poll.elapsed_ms}
    if ack.poll.status.state != AxisState.DISCRETE_MOTION:
        what = "the MoveAbsolute ack did not show its state (ack in the scan that enters it)"
        return mismatch(ctx, what, Read("State", ack.poll.status.state, int(AxisState.DISCRETE_MOTION)), **observed)
    budget_s = travel_timeout_s(target - start.actual_position, velocity)
    done = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state == AxisState.ERROR_STOP or (s.state == AxisState.STANDSTILL and s.in_position),
        budget_s,
        since=ack.written_at,
    )
    s = done.status
    error = abs(s.actual_position - target)
    observed |= {"arrivedAfterMs": done.elapsed_ms, "position": s.actual_position, "positionError": error}
    if not done.met or s.state != AxisState.STANDSTILL:
        if s.state == AxisState.ERROR_STOP:
            return faulted(ctx, "MoveAbsolute ended in ErrorStop", s, **observed)
        reads = (
            Read("State", s.state, int(AxisState.STANDSTILL)),
            Read("Flags", int(s.flags)),
            Read("ActualPosition", s.actual_position, target),
        )
        # Review #10: the FAIL names the budget used (protocol CHK-13).
        what = (
            f"not arrived in position within {budget_s:.1f} s (2 × |target − start| ÷ velocity + {STATE_TIMEOUT_S:g} s)"
        )
        return motion_failed(ctx, what, *reads, **observed)
    tolerance = round(ctx.options.tolerance * UNITS)
    if error > tolerance:
        what = f"stopped outside the in-position tolerance: error {error / UNITS:.3f} > {ctx.options.tolerance:g}"
        return motion_failed(ctx, what, Read("ActualPosition", s.actual_position, target), **observed)
    return passed(f"in position, error {error / UNITS:.3f}, {done.elapsed_ms / 1000:.2f} s", **observed)


async def stop_and_measure(
    ctx: CheckContext, observed: dict[str, int | None], *, require_zero_velocity: bool, ack_key: str
) -> Outcome | None:
    """Write Stop (priority edge) and wait ≤ 200 ms for Standstill (CHK-14, CHK-15)."""
    ack = await ctx.command(ctx.enabled | Command.STOP)
    observed[ack_key] = ack.poll.elapsed_ms

    def halted(s: StatusBlock) -> bool:
        return s.state == AxisState.STANDSTILL and (s.actual_velocity == 0 or not require_zero_velocity)

    halt = await wait_for(ctx.client, ctx.registers, halted, STOP_HALT_S, since=ack.written_at)
    observed["haltMs"] = halt.elapsed_ms
    if not halt.met:
        # Keep watching, so the report says how long it did take.
        late = await wait_for(ctx.client, ctx.registers, halted, STATE_TIMEOUT_S, since=ack.written_at)
        observed["haltMs"] = late.elapsed_ms
        outcome = (
            f"halted after {late.elapsed_ms} ms"
            if late.met
            else f"still moving after the checker's {STATE_TIMEOUT_S:g} s watch"
        )
        what = f"still moving {ms(STOP_HALT_S)} ms after the Stop write ({outcome})"
        reads = (
            Read("State", late.status.state, int(AxisState.STANDSTILL)),
            Read("ActualVelocity", late.status.actual_velocity),
        )
        return motion_failed(ctx, what, *reads, **observed)
    return None


async def chk14(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    target = start.travel_min + (start.travel_max - start.travel_min) // 2
    velocity = percent_of(start.max_velocity, DISCRETE_SPEED_PERCENT)
    await ctx.write_parameters(target, velocity, 0)
    move = await ctx.command(Command.ENABLE | Command.MOVE_ABSOLUTE)
    if move.poll.status.state != AxisState.DISCRETE_MOTION:
        what = "the MoveAbsolute ack did not show its state (ack in the scan that enters it)"
        read = Read("State", move.poll.status.state, int(AxisState.DISCRETE_MOTION))
        return mismatch(ctx, what, read, commandedVelocity=velocity)
    cruise = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state != AxisState.DISCRETE_MOTION or abs(s.actual_velocity) >= CRUISE_FRACTION * velocity,
        CRUISE_WAIT_S,
    )
    observed: dict[str, int | None] = {"commandedVelocity": velocity, "velocityAtStop": cruise.status.actual_velocity}
    if cruise.status.state != AxisState.DISCRETE_MOTION:
        if cruise.status.state == AxisState.ERROR_STOP:
            return faulted(ctx, "the move ended in ErrorStop before Stop", cruise.status, **observed)
        read = Read("State", cruise.status.state, int(AxisState.DISCRETE_MOTION))
        return motion_failed(ctx, "the move ended before Stop", read, **observed)
    problem = await stop_and_measure(ctx, observed, require_zero_velocity=True, ack_key="ackMs")
    if problem:
        return problem
    return passed(f"halted {observed['haltMs']} ms after Stop", **observed)


async def chk15(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    velocity = percent_of(start.max_velocity, JOG_SPEED_PERCENT)
    await ctx.write_parameters(start.actual_position, velocity, 0)
    ack = await ctx.command(Command.ENABLE | Command.MOVE_VELOCITY)
    observed: dict[str, int | None] = {"commandedVelocity": velocity, "ackMs": ack.poll.elapsed_ms}
    if ack.poll.status.state != AxisState.CONTINUOUS_MOTION:
        what = "the MoveVelocity ack did not show its state (ack in the scan that enters it)"
        return mismatch(ctx, what, Read("State", ack.poll.status.state, int(AxisState.CONTINUOUS_MOTION)), **observed)
    peak = 0
    left_state: int | None = None
    ends = ack.written_at + JOG_RUN_S
    while time.monotonic() < ends:
        s = await ctx.status()
        peak = max(peak, s.actual_velocity)
        if s.state != AxisState.CONTINUOUS_MOTION:
            left_state = s.state
            break
        await asyncio.sleep(POLL_PERIOD_S)
    observed["maxVelocitySeen"] = peak
    if left_state is not None:
        read = Read("State", left_state, int(AxisState.CONTINUOUS_MOTION))
        return motion_failed(ctx, "left ContinuousMotion during the run", read, **observed)
    if peak <= 0:
        return motion_failed(ctx, "ActualVelocity never > 0 during the run", Read("ActualVelocity", peak), **observed)
    problem = await stop_and_measure(ctx, observed, require_zero_velocity=False, ack_key="stopAckMs")
    if problem:
        return problem
    return passed(f"jogged at up to {peak / UNITS:g}/s; Standstill {observed['haltMs']} ms after Stop", **observed)


async def chk16(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    velocity = percent_of(start.max_velocity, JOG_SPEED_PERCENT)
    await ctx.write_parameters(start.actual_position, velocity, 0)
    jog = await ctx.command(Command.ENABLE | Command.MOVE_VELOCITY)
    moving = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state != AxisState.CONTINUOUS_MOTION or s.actual_velocity > 0,
        MOTION_START_S,
        since=jog.written_at,
    )
    observed = {"commandedVelocity": velocity}
    if moving.status.state != AxisState.CONTINUOUS_MOTION or moving.status.actual_velocity <= 0:
        reads = (
            Read("State", moving.status.state, int(AxisState.CONTINUOUS_MOTION)),
            Read("ActualVelocity", moving.status.actual_velocity),
        )
        what = f"ActualVelocity not > 0 within {MOTION_START_S:g} s of the MoveVelocity ack, before the kill"
        return motion_failed(ctx, what, *reads, **observed)
    last_beat = await ctx.beater.stop_beating()
    watch = await watch_trip(ctx, last_beat, 0, need_latch=False)
    observed |= {"tripAfterMs": watch.after_ms}
    problem = judge_trip(ctx, watch, "kill", **observed)
    if problem:
        return problem
    halt = await wait_for(ctx.client, ctx.registers, lambda s: s.actual_velocity == 0, STOP_HALT_S, since=watch.stamp)
    observed |= {"haltAfterTripMs": halt.elapsed_ms, "homedAfterTrip": int(halt.status.homed)}
    if not halt.met:
        what = f"still moving {ms(STOP_HALT_S)} ms after the trip"
        return motion_failed(ctx, what, Read("ActualVelocity", halt.status.actual_velocity, 0), **observed)
    if not halt.status.homed:
        return mismatch(ctx, "the watchdog trip cleared Homed", Read("Flags", int(halt.status.flags)), **observed)
    return passed(f"tripped {trip_interval(watch)}, halted {halt.elapsed_ms} ms later, still homed", **observed)


CHECKS: tuple[Check, ...] = (
    Check("CHK-01", "Transport and unit", "Transport", (), False, chk01),
    Check("CHK-02", "Map version", "Status block", ("01",), False, chk02),
    Check("CHK-03", "Machine limits published", 'Status block, "Limits come from the machine"', ("02",), False, chk03),
    Check("CHK-04", "Status mirror cadence", "Status block; FR-11 tick", ("02",), False, chk04),
    Check(
        "CHK-05",
        "32-bit word order and driver ownership of parameters",
        "Transport (word order); Command block",
        ("02",),
        False,
        chk05,
    ),
    Check("CHK-06", "Enable handshake (level)", "Command semantics: Handshake, Enable", ("02",), False, chk06),
    Check("CHK-07", "Reset handshake (edge)", "Command semantics: Reset, Acknowledge", ("06",), False, chk07),
    Check("CHK-08", "Watchdog trips on a stalled beat", "FR-11", ("06",), False, chk08),
    Check("CHK-09", "Watchdog disarms after a trip and re-arms on clear", "FR-11", ("08",), False, chk09),
    Check("CHK-10", "Clean release disarms", 'FR-11 "Clean release disarms"', ("08",), False, chk10),
    Check("CHK-11", "Advisory lease", 'FR-11 "Advisory lease"', ("02",), False, chk11),
    Check("CHK-12", "Home", "Command semantics: Home", ("06",), True, chk12),
    Check("CHK-13", "MoveAbsolute to TravelMin + 10", "Command semantics: MoveAbsolute", ("03", "12"), True, chk13),
    Check("CHK-14", "Stop mid-move", "Command semantics: Stop; FR-11 priority", ("13",), True, chk14),
    Check("CHK-15", "MoveVelocity", "Command semantics: MoveVelocity", ("13",), True, chk15),
    Check("CHK-16", "Kill test", "FR-11", ("08", "15"), True, chk16),
)
