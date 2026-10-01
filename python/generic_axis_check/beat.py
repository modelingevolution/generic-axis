"""``Beater``: the checker's own heartbeat loop (protocol.md § Conformance checks, rule "Beat")."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import time
from collections.abc import Callable

from .client import PlcClient, PlcError
from .registers import RegisterMap, next_nonzero, register_ref

log = logging.getLogger(__name__)

BEAT_PERIOD_S = 0.1
"""protocol.md § Conformance checks, "Beat": the checker writes ``Heartbeat`` every 100 ms."""


class LeaseLost(Exception):  # noqa: N818 — the condition's name in the lead ruling (#33)
    """A beating checker read ``LeaseOwner`` ≠ its own id: Protocol/ProtocolMismatch, the register does not hold what
    was written."""

    def __init__(self, owner: int, expected: int, registers: RegisterMap) -> None:
        self.owner = owner
        self.expected = expected
        where = register_ref(registers, "LeaseOwner")
        super().__init__(f"the lease did not hold. Read LeaseOwner ({where}) = {owner}, expected {expected}.")


class Beater:
    """Writes an incrementing ``Heartbeat`` (1…65535, never 0) every 100 ms until stopped.

    ``last_beat`` is the ``time.monotonic()`` at which the last beat write completed: every watchdog timing is
    measured from it. A beat write that fails after the client's one retry is logged at Warning and ends the loop
    (protocol.md § Errors and debugging, rule 3). ``stop()`` re-raises it, and ``raise_if_failed()`` lets every wait
    report it as Transport before the PLC's watchdog trip can be mistaken for a Machine fault (rule 2; review #7).
    """

    def __init__(
        self,
        client: PlcClient,
        registers: RegisterMap,
        period_s: float = BEAT_PERIOD_S,
        expected_owner: Callable[[], int | None] | None = None,
    ) -> None:
        self._client = client
        self._registers = registers
        self._period_s = period_s
        self._task: asyncio.Task[None] | None = None
        self._value = 0
        self.last_beat: float | None = None
        self.expected_owner = expected_owner
        """The id ``LeaseOwner`` must hold while this beat runs (None: not checked). protocol.md § Rules for every run,
        "Lease and beat between checks": a beating checker that reads another owner has lost the axis."""
        self.lease_lost: LeaseLost | None = None
        self._stopping = asyncio.Event()

    @property
    def running(self) -> bool:
        return self._task is not None and not self._task.done()

    async def start(self) -> None:
        if self.running:
            return
        (current,) = await self._client.read(self._registers.heartbeat, 1)
        self._value = current
        await self._beat()  # the first beat is written before start() returns, so the PLC sees a change now
        self._stopping = asyncio.Event()
        self._task = asyncio.create_task(self._loop(self._stopping), name="heartbeat")

    async def _beat(self) -> None:
        self._value = next_nonzero(self._value)
        await self._client.write(self._registers.heartbeat, [self._value])
        self.last_beat = time.monotonic()

    async def _loop(self, stopping: asyncio.Event) -> None:
        next_at = time.monotonic() + self._period_s
        while True:
            # Review #30: stop only between beats. A stop that cancelled a write already on the wire left last_beat
            # one beat early (the PLC saw that write), so every trip timing measured from it came 100 ms short.
            with contextlib.suppress(TimeoutError):
                async with asyncio.timeout(max(0.0, next_at - time.monotonic())):
                    await stopping.wait()
                return
            next_at += self._period_s
            try:
                await self._beat()
                if await self._lease_lost():
                    return  # the axis has another owner: stop beating, write nothing more
            except PlcError as exc:
                log.warning("%s: %s", self._beat_failed(), exc)
                raise

    async def _lease_lost(self) -> bool:
        expected = self.expected_owner() if self.expected_owner is not None else None
        if expected is None:
            return False
        (owner,) = await self._client.read(self._registers.lease_owner, 1)
        if owner == expected:
            return False
        self.lease_lost = LeaseLost(owner, expected, self._registers)
        log.warning("%s The beat stopped; nothing more is written to this axis.", self.lease_lost)
        return True

    async def stop(self) -> float | None:
        """Stop beating; returns ``last_beat``. Re-raises a beat write failure as ``PlcError``."""
        task, self._task = self._task, None
        if task is None:
            return self.last_beat
        if task.done():
            exc = task.exception()
            if exc is not None:
                raise PlcError(f"heartbeat loop failed: {exc}") from exc
            return self.last_beat
        self._stopping.set()  # the loop ends after the write in flight, if any, has completed
        try:
            await task
        except PlcError as exc:
            raise PlcError(f"heartbeat loop failed: {exc}") from exc
        return self.last_beat

    async def stop_beating(self) -> float:
        """Stop a beat that ran; returns the time of its last write (every watchdog timing starts there)."""
        last_beat = await self.stop()
        if last_beat is None:
            raise RuntimeError("the beat was stopped before it ever wrote Heartbeat")
        return last_beat

    def raise_if_failed(self) -> None:
        """Raise ``LeaseLost`` if the beat read another owner, or the beat's failure as ``PlcError`` (Transport)."""
        if self.lease_lost is not None:
            raise self.lease_lost
        failure = self.failure()
        if failure is not None:
            raise PlcError(f"{self._beat_failed()}: {failure}") from failure

    def _beat_failed(self) -> str:
        return f"Heartbeat ({register_ref(self._registers, 'Heartbeat')}) write failed, the beat stopped"

    def failure(self) -> BaseException | None:
        """The exception that ended the loop, if it ended by itself."""
        if self._task is not None and self._task.done() and not self._task.cancelled():
            return self._task.exception()
        return None
