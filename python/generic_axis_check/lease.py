"""The FR-11 advisory lease (protocol.md § FR-11, "Advisory lease"), used by CHK-06, CHK-10 and CHK-11."""

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass

from .client import PlcClient
from .registers import RegisterMap

LEASE_EXPIRY_S = 1.0
"""protocol.md § FR-11: a foreign lease whose ``Heartbeat`` is unchanged for one expiry window (1 s) has expired."""

WATCH_PERIOD_S = 0.1
"""protocol.md § FR-11: a foreign owner's ``Heartbeat`` is watched at the heartbeat interval (the driver's 10 Hz)."""


class LeaseHeld(Exception):  # noqa: N818 — the SDK's name (protocol.md § FR-11, "refuse (SDK LeaseHeld)")
    """SDK ``LeaseHeld``: a foreign owner is still beating after the lease timeout."""

    def __init__(self, owner: int) -> None:
        super().__init__(f"lease held by owner {owner}")
        self.owner = owner


def evaluate(owner: int, age_s: float, expiry_s: float, mine: int) -> bool:
    """The lease rule: 0 or own id → granted; a foreign id → granted once its beat is ``expiry_s`` old."""
    return owner == 0 or owner == mine or age_s >= expiry_s


@dataclass(frozen=True, slots=True)
class LeaseTaken:
    taken_at: float
    """``time.monotonic()`` when the read-back confirmed the lease."""
    previous_owner: int


async def acquire(
    client: PlcClient,
    registers: RegisterMap,
    owner_id: int,
    timeout_s: float,
    *,
    expiry_s: float = LEASE_EXPIRY_S,
    period_s: float = WATCH_PERIOD_S,
) -> LeaseTaken:
    """Take the lease, watching a foreign owner's beat continuously; raise ``LeaseHeld`` after ``timeout_s``.

    Reads ``Heartbeat`` and ``LeaseOwner`` (C+8, C+9) together, so the age is measured from the last observed change.
    """
    start = time.monotonic()
    deadline = start + timeout_s
    seen_owner: int | None = None
    seen_beat = 0
    changed_at = start
    while True:
        beat, owner = await client.read(registers.heartbeat, 2)
        now = time.monotonic()
        if owner != seen_owner or beat != seen_beat:
            seen_owner, seen_beat, changed_at = owner, beat, now
        if evaluate(owner, now - changed_at, expiry_s, owner_id):
            await client.write(registers.lease_owner, [owner_id])
            (read_back,) = await client.read(registers.lease_owner, 1)
            if read_back != owner_id:
                raise LeaseHeld(read_back)
            return LeaseTaken(time.monotonic(), owner)
        if now >= deadline:
            raise LeaseHeld(owner)
        await asyncio.sleep(period_s)
