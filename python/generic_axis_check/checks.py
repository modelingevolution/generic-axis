"""``CHECKS``: CHK-01…CHK-16 of protocol.md § Conformance checks, in id order, one function per check.

Every threshold below cites the table row it comes from. Positions and velocities are raw register values
(0.001 axis unit); the report carries them raw (protocol.md § Report schema: "Numbers are raw integers or durations").
"""

from __future__ import annotations

import asyncio
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
from .lease import LeaseHeld, acquire
from .poll import POLL_PERIOD_S, ms, wait_for
from .registers import (
    MAP_VERSION,
    VALID_STATES,
    AxisState,
    Command,
    FaultCode,
    StatusBlock,
    to_words,
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
"""CHK-14: write Stop once ``abs(ActualVelocity)`` ≥ 90 % of the commanded speed, or after 2 s. CHK-16 reuses the
2 s bound to wait for motion before it stops beating, so the kill lands on a moving axis."""

JOG_RUN_S = 1.0
"""CHK-15: MoveVelocity for 1 s, then Stop."""


def travel_timeout_s(distance: int, velocity: int) -> float:
    """Arrival bound for CHK-13/14. The protocol sets none: the checker allows twice the constant-speed travel time
    plus CHK-06's 5 s state budget, so a ramp never fails the check and a stalled axis still ends it."""
    return 2 * abs(distance) / max(velocity, 1) + STATE_TIMEOUT_S


@dataclass(slots=True)
class Outcome:
    result: str
    message: str
    observed: dict[str, int] = field(default_factory=dict)


def passed(message: str, **observed: int) -> Outcome:
    return Outcome(PASS, message, dict(observed))


def failed(message: str, **observed: int) -> Outcome:
    return Outcome(FAIL, message, dict(observed))


Run = Callable[[CheckContext], Awaitable[Outcome]]


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
            return TripWatch(tripped, ms(stamp - last_beat), status, fault, trips, stamp)
        await asyncio.sleep(POLL_PERIOD_S)


def judge_trip(watch: TripWatch, label: str) -> str | None:
    """The failure message for a trip outside the 1.0–1.5 s window, or None when it is inside."""
    if not watch.met:
        return (
            f"{label}: no trip within {TRIP_WINDOW_MS[1] / 1000:g} s of the last beat "
            f"(State {watch.status.state}, FaultCode {watch.status.fault_code}, WatchdogFault {watch.watchdog_fault}, "
            f"WatchdogTrips {watch.trips})"
        )
    if watch.after_ms < TRIP_WINDOW_MS[0]:
        return f"{label}: tripped {watch.after_ms} ms after the last beat, before the 1 s stall window"
    return None


async def beat_for(ctx: CheckContext, seconds: float) -> None:
    """Make sure the checker's beat has run for ``seconds`` (starting it if needed)."""
    await ctx.beater.start()
    started = ctx.beater.started_at or time.monotonic()
    await asyncio.sleep(max(0.0, started + seconds - time.monotonic()))
    failure = ctx.beater.failure()
    if failure is not None:
        raise PlcError(f"heartbeat loop failed: {failure}")


async def ensure_enabled(ctx: CheckContext) -> Outcome | None:
    """Enable and wait for Standstill (CHK-06's 5 s budget). Returns a failure, or None when the axis is ready."""
    status = await ctx.status()
    if status.state == AxisState.STANDSTILL and ctx.enabled:
        return None
    ack = await ctx.command(Command.ENABLE)
    poll = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state == AxisState.STANDSTILL, STATE_TIMEOUT_S, since=ack.written_at
    )
    if not poll.met:
        return failed(
            f"Enable: State {poll.status.state} after {STATE_TIMEOUT_S:g} s, expected 1",
            state=poll.status.state,
            faultCode=poll.status.fault_code,
        )
    return None


def percent_of(value: int, percent: int) -> int:
    return value * percent // 100


# --------------------------------------------------------------------------------------------------- checks


async def chk01(ctx: CheckContext) -> Outcome:
    if not ctx.client.connected:
        await ctx.client.connect()
    started = time.monotonic()
    await ctx.status()
    return passed(
        f"connected to {ctx.client.host}:{ctx.client.port}, unit {ctx.client.unit} answers",
        roundTripMs=ms(time.monotonic() - started),
    )


async def chk02(ctx: CheckContext) -> Outcome:
    (version,) = await ctx.client.read(ctx.registers.map_version, 1)
    if version != MAP_VERSION:
        return failed(f"MapVersion {version}, expected {MAP_VERSION}", mapVersion=version)
    return passed(f"MapVersion {version}", mapVersion=version)


async def chk03(ctx: CheckContext) -> Outcome:
    s = await ctx.status()
    observed = {"travelMin": s.travel_min, "travelMax": s.travel_max, "maxVelocity": s.max_velocity}
    values = f"TravelMin {s.travel_min}, TravelMax {s.travel_max}, MaxVelocity {s.max_velocity}"
    if s.travel_min == 0 and s.travel_max == 0 and s.max_velocity == 0:
        return failed(f"limits not published: {values}", **observed)
    problems = []
    if not s.travel_min < s.travel_max:
        problems.append("TravelMin is not < TravelMax")
    if not s.max_velocity > 0:
        problems.append("MaxVelocity is not > 0")
    if problems:
        return failed(f"{'; '.join(problems)}: {values}", **observed)
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
    observed = {"reads": CADENCE_READS, "slowestRoundTripMs": slowest, "invalidStates": len(bad_states)}
    if bad_states:
        return failed(f"State outside {{0,1,2,3,4,6,7}}: {sorted(set(bad_states))}", **observed)
    if slowest > CADENCE_MAX_ROUND_TRIP_MS:
        return failed(f"slowest round trip {slowest} ms > {CADENCE_MAX_ROUND_TRIP_MS} ms", **observed)
    return passed(f"{CADENCE_READS} reads, slowest {slowest} ms", **observed)


async def chk05(ctx: CheckContext) -> Outcome:
    address = ctx.registers.target_position
    observed: dict[str, int] = {}
    for index, (value, words) in enumerate(WORD_ORDER_VALUES, start=1):
        assert to_words(value) == words  # the table's words are the codec's words, by construction
        await ctx.client.write(address, list(words))
        back = await ctx.client.read(address, 2)
        observed[f"readBack{index}Low"], observed[f"readBack{index}High"] = back
        if tuple(back) != words:
            return failed(f"wrote {value} as {list(map(hex, words))}, read back {list(map(hex, back))}", **observed)
    await asyncio.sleep(OWNERSHIP_WAIT_S)
    back = await ctx.client.read(address, 2)
    observed["afterWaitLow"], observed["afterWaitHigh"] = back
    last_words = WORD_ORDER_VALUES[-1][1]
    if tuple(back) != last_words:
        return failed(
            f"C+2..3 changed to {list(map(hex, back))} within {OWNERSHIP_WAIT_S:g} s: the PLC wrote a "
            "driver-owned register",
            **observed,
        )
    return passed("65538 and -2 read back exactly and stayed", **observed)


async def chk06(ctx: CheckContext) -> Outcome:
    await ctx.take_lease()
    ctx.session_lease = True
    fault, _ = await ctx.watchdog()
    if fault:
        await ctx.clear_watchdog_fault()  # FR-11 "At attach": a latched trip belongs to a dead predecessor
    await ctx.beater.start()
    status = await ctx.status()
    if status.state not in (AxisState.DISABLED, AxisState.STANDSTILL):
        status = await ctx.recover()
    if status.state not in (AxisState.DISABLED, AxisState.STANDSTILL):
        return failed(f"precondition: State {status.state}, expected 0 or 1", state=status.state)

    on = await ctx.command(Command.ENABLE)
    on_state = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state == AxisState.STANDSTILL, STATE_TIMEOUT_S, since=on.written_at
    )
    observed = {"enableAckMs": on.poll.elapsed_ms, "enableStateMs": on_state.elapsed_ms}
    if not on_state.met:
        return failed(f"Enable 1: State {on_state.status.state} after {STATE_TIMEOUT_S:g} s, expected 1", **observed)
    off = await ctx.command(Command.NONE)
    off_state = await wait_for(
        ctx.client, ctx.registers, lambda s: s.state == AxisState.DISABLED, STATE_TIMEOUT_S, since=off.written_at
    )
    observed |= {"disableAckMs": off.poll.elapsed_ms, "disableStateMs": off_state.elapsed_ms}
    if not off_state.met:
        return failed(f"Enable 0: State {off_state.status.state} after {STATE_TIMEOUT_S:g} s, expected 0", **observed)
    return passed(
        f"Enable ack {on.poll.elapsed_ms} ms, Standstill {on_state.elapsed_ms} ms; "
        f"disable ack {off.poll.elapsed_ms} ms, Disabled {off_state.elapsed_ms} ms",
        **observed,
    )


async def chk07(ctx: CheckContext) -> Outcome:
    status = await ctx.status()
    if status.state != AxisState.DISABLED or status.fault_code != FaultCode.NONE:
        return failed(
            f"precondition: State {status.state}, FaultCode {status.fault_code}, expected 0 and 0",
            state=status.state,
            faultCode=status.fault_code,
        )
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
        return failed(
            f"Reset outside ErrorStop changed the axis: State {changed.status.state}, "
            f"FaultCode {changed.status.fault_code}",
            **observed,
        )
    return passed(f"ack {ack.poll.elapsed_ms} ms, no-op", **observed)


async def chk08(ctx: CheckContext) -> Outcome:
    await ctx.clear_watchdog_fault()
    _, trips_before = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    last_beat = await ctx.beater.stop()
    assert last_beat is not None
    watch = await watch_trip(ctx, last_beat, trips_before)
    observed = {
        "tripAfterMs": watch.after_ms,
        "watchdogTrips": watch.trips,
        "faultCode": watch.status.fault_code,
        "state": watch.status.state,
        "watchdogFault": watch.watchdog_fault,
    }
    problem = judge_trip(watch, "stalled beat")
    if problem:
        return failed(problem, **observed)
    return passed(f"trip after {watch.after_ms / 1000:.2f} s", **observed)


async def chk09(ctx: CheckContext) -> Outcome:
    # The trip to start from: every check restores (protocol rule), so CHK-09 causes its own first trip.
    await ctx.clear_watchdog_fault()
    _, trips0 = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    first = await watch_trip(ctx, await ctx.beater.stop() or time.monotonic(), trips0)
    observed = {"firstTripMs": first.after_ms}
    problem = judge_trip(first, "first trip")
    if problem:
        return failed(problem, **observed)
    trips1 = first.trips

    # Latched: beat without clearing, then stall again; the network must stay disarmed.
    await ctx.beater.start()
    await asyncio.sleep(LATCHED_BEAT_S)
    await ctx.beater.stop()
    await asyncio.sleep(TRIP_WINDOW_MS[1] / 1000)
    _, trips_latched = await ctx.watchdog()
    observed["tripsWhileLatched"] = (trips_latched - trips1) & 0xFFFF
    if trips_latched != trips1:
        return failed(f"a trip was counted while WatchdogFault was latched ({trips1} → {trips_latched})", **observed)

    # Re-arm: Reset edge, WatchdogFault = 0, beat 2 s (no trip), stall → second trip.
    await ctx.command(Command.RESET)
    await ctx.clear_watchdog_fault()
    ctx.caused_trip = False
    await beat_for(ctx, ARMED_BEAT_S)
    fault, trips_beating = await ctx.watchdog()
    observed["tripsWhileBeating"] = (trips_beating - trips1) & 0xFFFF
    if fault or trips_beating != trips1:
        return failed(
            f"tripped while beating (WatchdogFault {fault}, WatchdogTrips {trips1} → {trips_beating})", **observed
        )
    second = await watch_trip(ctx, await ctx.beater.stop() or time.monotonic(), trips1)
    observed |= {"secondTripMs": second.after_ms, "watchdogTrips": second.trips}
    problem = judge_trip(second, "after clear")
    if problem:
        return failed(problem, **observed)
    return passed(
        f"disarmed while latched; re-armed on clear, second trip after {second.after_ms / 1000:.2f} s", **observed
    )


async def chk10(ctx: CheckContext) -> Outcome:
    await ctx.clear_watchdog_fault()
    _, trips_before = await ctx.watchdog()
    await beat_for(ctx, ARMED_BEAT_S)
    await ctx.client.write(ctx.registers.lease_owner, [0])
    ctx.holds_lease = False
    await ctx.beater.stop()
    await asyncio.sleep(RELEASE_WAIT_S)
    fault, trips = await ctx.watchdog()
    observed = {"watchdogFault": fault, "tripsDelta": (trips - trips_before) & 0xFFFF}
    if fault or trips != trips_before:
        return failed(
            f"tripped after a clean release (WatchdogFault {fault}, WatchdogTrips " f"{trips_before} → {trips})",
            **observed,
        )
    return passed(f"no trip {RELEASE_WAIT_S:g} s after LeaseOwner = 0", **observed)


async def chk11(ctx: CheckContext) -> Outcome:
    registers, own = ctx.registers, ctx.options.owner_id
    if ctx.holds_lease:
        await ctx.release_lease()
    observed: dict[str, int] = {}

    # (a) unowned → taken, read back, released.
    await ctx.client.write(registers.lease_owner, [0])
    await acquire(ctx.client, registers, own, LEASE_TIMEOUT_S)
    (read_back,) = await ctx.client.read(registers.lease_owner, 1)
    observed["aReadBack"] = read_back
    await ctx.client.write(registers.lease_owner, [0])
    if read_back != own:
        return failed(f"(a) LeaseOwner read back {read_back}, expected {own}", **observed)

    incumbent = Beater(ctx.client, registers)
    try:
        # (b) a live foreign incumbent → refused after the 3 s timeout, its lease untouched.
        await ctx.client.write(registers.lease_owner, [FOREIGN_OWNER_ID])
        await incumbent.start()
        started = time.monotonic()
        try:
            await acquire(ctx.client, registers, own, LEASE_TIMEOUT_S)
        except LeaseHeld as held:
            observed["bRefusedAfterMs"] = ms(time.monotonic() - started)
            observed["bRefusedOwner"] = held.owner
        else:
            ctx.holds_lease = True
            return failed(f"(b) took the lease from a beating owner {FOREIGN_OWNER_ID}", **observed)
        (owner_b,) = await ctx.client.read(registers.lease_owner, 1)
        observed["bLeaseOwner"] = owner_b
        if observed["bRefusedOwner"] != FOREIGN_OWNER_ID or owner_b != FOREIGN_OWNER_ID:
            return failed(
                f"(b) refusal named {observed['bRefusedOwner']}, LeaseOwner {owner_b}; expected " f"{FOREIGN_OWNER_ID}",
                **observed,
            )

        # (c) the incumbent dies while the client watches → taken within 2 s of its last beat.
        take = asyncio.create_task(acquire(ctx.client, registers, own, LEASE_TIMEOUT_S))
        await asyncio.sleep(WATCH_BEFORE_STALL_S)
        last_beat = await incumbent.stop()
        assert last_beat is not None
        try:
            taken = await take
        except LeaseHeld as held:
            return failed(f"(c) not taken: still held by {held.owner} after {LEASE_TIMEOUT_S:g} s", **observed)
        ctx.holds_lease = True
        observed["cTakenAfterMs"] = ms(taken.taken_at - last_beat)
    finally:
        await incumbent.stop()
        # "Any trip caused by (c) is cleaned up": the incumbent's stall arms and trips the PLC watchdog.
        fault, _ = await ctx.watchdog()
        if fault:
            ctx.caused_trip = True
            await ctx.recover()
        if not ctx.session_lease and ctx.holds_lease:
            await ctx.release_lease()

    if observed["cTakenAfterMs"] > LEASE_TAKEOVER_MS:
        return failed(
            f"(c) taken {observed['cTakenAfterMs']} ms after the incumbent's last beat, > " f"{LEASE_TAKEOVER_MS} ms",
            **observed,
        )
    return passed(
        f"(a) {read_back}; (b) refused, LeaseHeld {FOREIGN_OWNER_ID}; (c) taken after "
        f"{observed['cTakenAfterMs']} ms",
        **observed,
    )


async def chk12(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    ack = await ctx.command(Command.ENABLE | Command.HOME)
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
        "homeMs": done.elapsed_ms,
        "faultCode": s.fault_code,
        "state": s.state,
        "homed": int(s.homed),
    }
    if not done.met:
        return failed(f"not homed after {HOME_TIMEOUT_S:g} s: State {s.state}", **observed)
    if s.state != AxisState.STANDSTILL or s.fault_code != FaultCode.NONE:
        return failed(f"homing ended in State {s.state}, FaultCode {s.fault_code}", **observed)
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
    observed = {
        "targetRaw": target,
        "velocityRaw": velocity,
        "ackMs": ack.poll.elapsed_ms,
        "ackState": ack.poll.status.state,
    }
    if ack.poll.status.state != AxisState.DISCRETE_MOTION:
        return failed(f"ack showed State {ack.poll.status.state}, expected 3", **observed)
    done = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state == AxisState.ERROR_STOP or (s.state == AxisState.STANDSTILL and s.in_position),
        travel_timeout_s(target - start.actual_position, velocity),
        since=ack.written_at,
    )
    s = done.status
    error = abs(s.actual_position - target)
    observed |= {"actualRaw": s.actual_position, "errorRaw": error, "durationMs": done.elapsed_ms}
    if not done.met or s.state != AxisState.STANDSTILL:
        return failed(
            f"not in position: State {s.state}, FaultCode {s.fault_code}, InPosition {int(s.in_position)}", **observed
        )
    tolerance = round(ctx.options.tolerance * UNITS)
    if error > tolerance:
        return failed(f"position error {error / UNITS:.3f} > tolerance {ctx.options.tolerance:g}", **observed)
    return passed(f"in position, error {error / UNITS:.3f}, {done.elapsed_ms / 1000:.2f} s", **observed)


async def stop_and_measure(
    ctx: CheckContext, observed: dict[str, int], *, require_zero_velocity: bool
) -> Outcome | None:
    """Write Stop (priority edge) and wait ≤ 200 ms for Standstill (CHK-14, CHK-15)."""
    ack = await ctx.command(ctx.enabled | Command.STOP)
    observed["stopAckMs"] = ack.poll.elapsed_ms

    def halted(s: StatusBlock) -> bool:
        return s.state == AxisState.STANDSTILL and (s.actual_velocity == 0 or not require_zero_velocity)

    halt = await wait_for(ctx.client, ctx.registers, halted, STOP_HALT_S, since=ack.written_at)
    observed["haltMs"] = halt.elapsed_ms
    if not halt.met:
        # Keep watching, so the report says how long it did take.
        late = await wait_for(ctx.client, ctx.registers, halted, STATE_TIMEOUT_S, since=ack.written_at)
        observed["haltMs"] = late.elapsed_ms
        return failed(
            f"not halted within {ms(STOP_HALT_S)} ms of the Stop write "
            f"({'after ' + str(late.elapsed_ms) + ' ms' if late.met else 'still moving'}: State "
            f"{late.status.state}, ActualVelocity {late.status.actual_velocity})",
            **observed,
        )
    return None


async def chk14(ctx: CheckContext) -> Outcome:
    not_ready = await ensure_enabled(ctx)
    if not_ready:
        return not_ready
    start = await ctx.status()
    target = start.travel_min + (start.travel_max - start.travel_min) // 2
    velocity = percent_of(start.max_velocity, DISCRETE_SPEED_PERCENT)
    await ctx.write_parameters(target, velocity, 0)
    await ctx.command(Command.ENABLE | Command.MOVE_ABSOLUTE)
    cruise = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state != AxisState.DISCRETE_MOTION or abs(s.actual_velocity) >= CRUISE_FRACTION * velocity,
        CRUISE_WAIT_S,
    )
    observed = {"targetRaw": target, "velocityRaw": velocity, "velocityAtStopRaw": cruise.status.actual_velocity}
    if cruise.status.state != AxisState.DISCRETE_MOTION:
        return failed(f"the move ended before Stop: State {cruise.status.state}", **observed)
    problem = await stop_and_measure(ctx, observed, require_zero_velocity=True)
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
    observed = {"velocityRaw": velocity, "ackMs": ack.poll.elapsed_ms, "ackState": ack.poll.status.state}
    if ack.poll.status.state != AxisState.CONTINUOUS_MOTION:
        return failed(f"ack showed State {ack.poll.status.state}, expected 4", **observed)
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
    observed["peakVelocityRaw"] = peak
    if left_state is not None:
        return failed(f"left ContinuousMotion during the run: State {left_state}", **observed)
    if peak <= 0:
        return failed("ActualVelocity never > 0 during the run", **observed)
    problem = await stop_and_measure(ctx, observed, require_zero_velocity=False)
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
    await ctx.command(Command.ENABLE | Command.MOVE_VELOCITY)
    moving = await wait_for(
        ctx.client,
        ctx.registers,
        lambda s: s.state != AxisState.CONTINUOUS_MOTION or s.actual_velocity > 0,
        CRUISE_WAIT_S,
    )
    observed = {"velocityRaw": velocity, "velocityAtKillRaw": moving.status.actual_velocity}
    if moving.status.state != AxisState.CONTINUOUS_MOTION or moving.status.actual_velocity <= 0:
        return failed(
            f"not moving before the kill: State {moving.status.state}, ActualVelocity "
            f"{moving.status.actual_velocity}",
            **observed,
        )
    last_beat = await ctx.beater.stop()
    assert last_beat is not None
    watch = await watch_trip(ctx, last_beat, 0, need_latch=False)
    observed |= {"tripAfterMs": watch.after_ms, "faultCode": watch.status.fault_code, "state": watch.status.state}
    problem = judge_trip(watch, "kill")
    if problem:
        return failed(problem, **observed)
    halt = await wait_for(ctx.client, ctx.registers, lambda s: s.actual_velocity == 0, STOP_HALT_S, since=watch.stamp)
    observed |= {"haltAfterTripMs": halt.elapsed_ms, "homed": int(halt.status.homed)}
    if not halt.met:
        return failed(f"ActualVelocity {halt.status.actual_velocity} {ms(STOP_HALT_S)} ms after the trip", **observed)
    if not halt.status.homed:
        return failed("the trip cleared Homed", **observed)
    return passed(
        f"trip after {watch.after_ms / 1000:.2f} s, halted {halt.elapsed_ms} ms later, still homed", **observed
    )


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
    Check("CHK-16", "Kill test", "FR-11", ("15",), True, chk16),
)
