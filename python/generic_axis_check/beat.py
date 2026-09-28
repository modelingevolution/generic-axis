"""``Beater``: the checker's own heartbeat loop (protocol.md § Conformance checks, rule "Beat")."""

from __future__ import annotations

import asyncio
import time

from .client import PlcClient, PlcError
from .registers import RegisterMap, next_nonzero

BEAT_PERIOD_S = 0.1
"""protocol.md § Conformance checks, "Beat": the checker writes ``Heartbeat`` every 100 ms."""


class Beater:
    """Writes an incrementing ``Heartbeat`` (1…65535, never 0) every 100 ms until stopped.

    ``last_beat`` is the ``time.monotonic()`` at which the last beat write completed: every watchdog timing is
    measured from it. A failed beat write stops the loop, and ``stop()`` re-raises it, so a flaky link is reported as
    such instead of as a PLC trip.
    """

    def __init__(self, client: PlcClient, registers: RegisterMap, period_s: float = BEAT_PERIOD_S) -> None:
        self._client = client
        self._registers = registers
        self._period_s = period_s
        self._task: asyncio.Task[None] | None = None
        self._value = 0
        self.last_beat: float | None = None
        self.started_at: float | None = None

    @property
    def running(self) -> bool:
        return self._task is not None and not self._task.done()

    async def start(self) -> None:
        if self.running:
            return
        (current,) = await self._client.read(self._registers.heartbeat, 1)
        self._value = current
        await self._beat()  # the first beat is written before start() returns, so the PLC sees a change now
        self.started_at = self.last_beat
        self._task = asyncio.create_task(self._loop(), name="heartbeat")

    async def _beat(self) -> None:
        self._value = next_nonzero(self._value)
        await self._client.write(self._registers.heartbeat, [self._value])
        self.last_beat = time.monotonic()

    async def _loop(self) -> None:
        next_at = time.monotonic() + self._period_s
        while True:
            await asyncio.sleep(max(0.0, next_at - time.monotonic()))
            next_at += self._period_s
            await self._beat()

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
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        return self.last_beat

    def failure(self) -> BaseException | None:
        """The exception that ended the loop, if it ended by itself."""
        if self._task is not None and self._task.done() and not self._task.cancelled():
            return self._task.exception()
        return None
