"""``PlcClient``: a thin pymodbus TCP wrapper that turns every failure into ``PlcError`` (design.md § Python)."""

from __future__ import annotations

import logging
import time

from pymodbus.client import AsyncModbusTcpClient
from pymodbus.exceptions import ModbusException

from .registers import STATUS_LENGTH, RegisterMap, StatusBlock

CONNECT_TIMEOUT_S = 2.0
"""CHK-01: "TCP connect (2 s timeout)"."""

REQUEST_TIMEOUT_S = 0.5
"""design.md § Python: request timeout 0.5 s (the protocol's ack budget; no single request may take longer)."""

log = logging.getLogger(__name__)


class PlcError(Exception):
    """A Modbus request failed: no connection, timeout, or a Modbus exception response."""


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
        # retries=0: a lost answer is reported as what it is, never hidden by a resend that shifts the timing.
        client = AsyncModbusTcpClient(self.host, port=self.port, timeout=REQUEST_TIMEOUT_S, retries=0)
        started = time.monotonic()
        try:
            ok = await client.connect()
        except (OSError, ModbusException) as exc:
            client.close()
            raise PlcError(f"connect to {self.host}:{self.port} failed: {exc}") from exc
        if not ok:
            client.close()
            raise PlcError(
                f"connect to {self.host}:{self.port} failed after {time.monotonic() - started:.1f} s"
            )
        self._client = client

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
            raise PlcError(f"FC03 {address}+{count}: {exc}") from exc
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
            raise PlcError(f"write {address}={values}: {exc}") from exc
        if response.isError():
            raise PlcError(f"write {address}={values}: Modbus exception {response}")
        log.debug("wrote %d = %s", address, values)

    async def read_status(self, registers: RegisterMap) -> StatusBlock:
        return StatusBlock.parse(await self.read(registers.status, STATUS_LENGTH))
