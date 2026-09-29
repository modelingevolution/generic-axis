"""``PlcClient``: a thin pymodbus TCP wrapper that turns every failure into ``PlcError`` (design.md § Python)."""

from __future__ import annotations

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable

from pymodbus.client import AsyncModbusTcpClient
from pymodbus.exceptions import ModbusException
from pymodbus.pdu import ModbusPDU

from .registers import STATUS_LENGTH, RegisterMap, StatusBlock, describe_range

CONNECT_ATTEMPTS = 2
CONNECT_ATTEMPT_S = 1.5
"""CHK-01: the TCP connect has a budget of ≤ 3 s including the tool's own retry: two attempts of 1.5 s."""

REQUEST_TIMEOUT_S = 0.5
"""design.md § Python: request timeout 0.5 s (the protocol's ack budget; no single request may take longer)."""

log = logging.getLogger(__name__)


class PlcError(Exception):
    """A Modbus request failed: no connection, timeout, or a Modbus exception response."""


def _failure(what: str, exc: BaseException) -> BaseException:
    """The exception to raise for a failed request.

    pymodbus catches a task cancellation inside a request and raises ``ModbusIOException`` ("Request cancelled
    outside library") instead. A Ctrl-C would then read as a Modbus failure and the run would go on commanding the
    axis, so a cancellation pending on the current task is turned back into ``CancelledError``.
    """
    task = asyncio.current_task()
    if task is not None and task.cancelling():
        return asyncio.CancelledError()
    return PlcError(f"{what}: {exc}")


class PlcClient:
    """Holding-register access to one unit. FC03 reads, FC06 for one register, FC16 for several (protocol.md)."""

    def __init__(self, host: str, port: int, unit: int, registers: RegisterMap | None = None) -> None:
        self.host = host
        self.port = port
        self.unit = unit
        self.registers = registers or RegisterMap()
        """Only for naming register ranges in error messages (protocol.md § Errors and debugging, rule 1)."""
        self._client: AsyncModbusTcpClient | None = None
        self.last_read: dict[int, int] = {}
        """The last value read from each register: the fallback ``lastRead`` evidence when a fresh read fails."""
        self.retries = 0
        """Reconnect-and-retries performed so far; each check reports its own delta (§ Observed values)."""
        self._generation = 0
        self._reconnect_lock = asyncio.Lock()

    def _where(self, operation: str, address: int, count: int) -> str:
        return (
            f"{operation} {describe_range(self.registers, address, count)} on {self.host}:{self.port} unit {self.unit}"
        )

    @property
    def connected(self) -> bool:
        return self._client is not None and self._client.connected

    async def connect(self) -> None:
        """Connect within CHK-01's budget. pymodbus uses one timeout for connect and for requests, so the client
        connects with the attempt timeout and then switches to the 0.5 s request timeout. Automatic reconnection is
        off (``reconnect_delay=0``): a lost link is reported, never silently recovered (protocol.md § Errors and
        debugging, rule 3)."""
        started = time.monotonic()
        last: BaseException | None = None
        for _ in range(CONNECT_ATTEMPTS):
            client = AsyncModbusTcpClient(
                self.host, port=self.port, timeout=CONNECT_ATTEMPT_S, retries=0, reconnect_delay=0
            )
            try:
                if await client.connect():
                    client.comm_params.timeout_connect = REQUEST_TIMEOUT_S
                    self._client = client
                    self._generation += 1
                    return
            except (OSError, ModbusException) as exc:
                last = exc
            client.close()
        reason = f": {last}" if last is not None else ""
        raise PlcError(
            f"connect to {self.host}:{self.port} failed ({CONNECT_ATTEMPTS} attempts in "
            f"{time.monotonic() - started:.1f} s){reason}"
        )

    def close(self) -> None:
        if self._client is not None:
            self._client.close()
            self._client = None

    def _require(self) -> AsyncModbusTcpClient:
        if self._client is None:
            raise PlcError(f"not connected to {self.host}:{self.port}")
        return self._client

    async def _reconnect(self, generation: int) -> None:
        """Replace the connection once per failure, even when the beat and a check fail together."""
        async with self._reconnect_lock:
            if generation != self._generation:
                return  # another caller already reconnected after the same failure
            self.close()
            await self.connect()

    async def _transact(
        self,
        where: str,
        request: Callable[[AsyncModbusTcpClient], Awaitable[ModbusPDU]],
        *,
        retry: bool,
    ) -> ModbusPDU:
        """One request with the protocol's one reconnect-and-retry (§ Errors and debugging, rule 3; § Error class of
        a FAIL, "The one retry"), logged at Warning and counted in ``retries``. ``retry=False`` for a command write:
        "A command is never re-sent"."""
        for attempt in range(2):
            generation = self._generation
            client = self._require()
            try:
                response = await request(client)
                if response.isError():
                    raise PlcError(f"{where} failed: Modbus exception {response}")
                return response
            except (OSError, ModbusException, PlcError) as exc:
                failure = exc if isinstance(exc, PlcError) else _failure(f"{where} failed", exc)
                if isinstance(failure, asyncio.CancelledError) or attempt == 1 or not retry:
                    raise failure from exc
                log.warning("%s; reconnecting and retrying once", failure)
                self.retries += 1
                try:
                    await self._reconnect(generation)
                except PlcError as again:
                    raise PlcError(f"{failure}; reconnect failed: {again}") from exc
        raise AssertionError("unreachable")

    async def read(self, address: int, count: int) -> list[int]:
        where = self._where("read", address, count)
        response = await self._transact(
            where, lambda c: c.read_holding_registers(address, count=count, device_id=self.unit), retry=True
        )
        registers = list(response.registers)
        if len(registers) != count:
            raise PlcError(f"{where} failed: answered {len(registers)} registers")
        for offset, value in enumerate(registers):
            self.last_read[address + offset] = value
        return registers

    async def write(self, address: int, values: list[int], *, retry: bool = True) -> None:
        where = f"{self._where('write', address, len(values))} = {values}"
        if len(values) == 1:
            await self._transact(
                where, lambda c: c.write_register(address, values[0], device_id=self.unit), retry=retry
            )
        else:
            await self._transact(where, lambda c: c.write_registers(address, values, device_id=self.unit), retry=retry)
        log.debug("wrote %d = %s", address, values)

    async def read_status(self, registers: RegisterMap) -> StatusBlock:
        return StatusBlock.parse(await self.read(registers.status, STATUS_LENGTH))
