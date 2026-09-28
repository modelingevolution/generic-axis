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
from .lease import LeaseHeld
from .poll import ms
from .registers import EDGE_BITS, MOVING_STATES, AxisState, Command, FaultCode, RegisterMap, next_nonzero

PREFLIGHT_WATCH_S = 1.0
"""protocol.md "Pre-flight": read ``LeaseOwner`` and watch ``Heartbeat`` for 1 s."""

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
    observed: dict[str, int] = field(default_factory=dict)


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


async def restore(ctx: CheckContext) -> str | None:
    """protocol.md "Each check restores": State 0 or 1, no latched fault, lease held and beat running.

    Returns why it could not, or None.
    """
    status = await ctx.recover()
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
        return f"axis left in State {status.state}, FaultCode {status.fault_code}"
    if fault:
        return "WatchdogFault still 1 after writing 0"
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
    client = PlcClient(options.host, options.port, options.unit)
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
            abort = (f"pre-flight: LeaseOwner {foreign} is beating: another commander is attached; stop it first")
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
            try:
                outcome = await _run_one(check, ctx)
                if check.id >= FIRST_LEASED_CHECK:
                    why = await _restore(ctx)
                    if why is not None:
                        outcome = Outcome(FAIL, f"{outcome.message}; restore failed: {why}" if outcome.result == FAIL
                                          else f"restore failed: {why}", outcome.observed)
                        abort = f"restore after {check.id} failed"
            except asyncio.CancelledError:
                task = asyncio.current_task()
                if task is not None:
                    task.uncancel()
                interrupted = True
                abort = "interrupted"
                outcome = Outcome(FAIL, "interrupted (Ctrl-C) during this check")
            result = CheckResult(check.id, check.title, check.section, outcome.result,
                                 ms(time.monotonic() - began), outcome.message, outcome.observed)
            results[check.id] = result
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

    return Report(options, started_at, datetime.now(UTC), [results[c.id] for c in checks], ctx.cleanup_log,
                  refused=refused, interrupted=interrupted)


async def _run_one(check: Check, ctx: CheckContext) -> Outcome:
    try:
        return await check.run(ctx)
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return Outcome(FAIL, str(exc))


async def _restore(ctx: CheckContext) -> str | None:
    if not ctx.client.connected:
        return "connection lost"
    try:
        return await restore(ctx)
    except (PlcError, AckTimeout, LeaseHeld) as exc:
        return str(exc)
