"""``wait_for``: status-block polling at the protocol's 20 ms cadence (protocol.md § Conformance checks, "Timing")."""

from __future__ import annotations

import asyncio
import time
from collections.abc import Callable
from dataclasses import dataclass

from .client import PlcClient
from .registers import RegisterMap, StatusBlock

POLL_PERIOD_S = 0.02
"""protocol.md § Conformance checks, "Timing": timing checks poll the status block every 20 ms."""


@dataclass(frozen=True, slots=True)
class PollResult:
    status: StatusBlock
    """The first status that met the predicate, or the last one read when it timed out."""
    elapsed_ms: int
    """From ``since`` to the read that met the predicate (or to the last read on timeout)."""
    met: bool


def ms(seconds: float) -> int:
    return round(seconds * 1000)


async def wait_for(
    client: PlcClient,
    registers: RegisterMap,
    predicate: Callable[[StatusBlock], bool],
    timeout_s: float,
    *,
    since: float | None = None,
    period_s: float = POLL_PERIOD_S,
) -> PollResult:
    """Poll S+0…S+14 (one FC04 each) every ``period_s`` until ``predicate`` holds or ``timeout_s`` passes after ``since``.

    ``since`` is the ``time.monotonic()`` of the triggering write's completion (default: now). A read is stamped when
    its answer arrives, so ``elapsed_ms`` is "completion of the write to the first read that shows the effect".
    """
    start = time.monotonic() if since is None else since
    deadline = start + timeout_s
    next_at = time.monotonic()
    while True:
        status = await client.read_status(registers)
        stamp = time.monotonic()
        if predicate(status):
            return PollResult(status, ms(stamp - start), True)
        if stamp >= deadline:
            return PollResult(status, ms(stamp - start), False)
        next_at += period_s
        await asyncio.sleep(max(0.0, min(next_at, deadline) - time.monotonic()))
