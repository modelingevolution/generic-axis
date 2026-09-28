"""``PlcClient``: a thin pymodbus TCP wrapper that turns every failure into ``PlcError`` (design.md § Python)."""

from __future__ import annotations

import asyncio
import logging
import time

from pymodbus.client import AsyncModbusTcpClient
from pymodbus.exceptions import ModbusException

from .registers import STATUS_LENGTH, RegisterMap, StatusBlock

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

    def __init__(self, host: str, port: int, unit: int) -> None:
        self.host = host
        self.port = port
        self.unit = unit
        self._client: AsyncModbusTcpClient | None = None
        self.writes = 0
        """Count of write requests sent, for tests that must prove "nothing written"."""

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
            raise PlcError("not connected")
        return self._client

    async def read(self, address: int, count: int) -> list[int]:
        client = self._require()
        try:
            response = await client.read_holding_registers(address, count=count, device_id=self.unit)
        except (OSError, ModbusException) as exc:
            raise _failure(f"FC03 {address}+{count}", exc) from exc
        if response.isError():
            raise PlcError(f"FC03 {address}+{count}: Modbus exception {response}")
        registers = list(response.registers)
        if len(registers) != count:
            raise PlcError(f"FC03 {address}+{count}: answered {len(registers)} registers")
        return registers

    async def write(self, address: int, values: list[int]) -> None:
        client = self._require()
        self.writes += 1
        try:
            if len(values) == 1:
                response = await client.write_register(address, values[0], device_id=self.unit)
            else:
                response = await client.write_registers(address, values, device_id=self.unit)
        except (OSError, ModbusException) as exc:
            raise _failure(f"write {address}={values}", exc) from exc
        if response.isError():
            raise PlcError(f"write {address}={values}: Modbus exception {response}")
        log.debug("wrote %d = %s", address, values)

    async def read_status(self, registers: RegisterMap) -> StatusBlock:
        return StatusBlock.parse(await self.read(registers.status, STATUS_LENGTH))
