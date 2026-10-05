"""``PlcClient``: a thin pymodbus TCP wrapper that turns every failure into ``PlcError`` (design.md § Python)."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import re
import time
from collections.abc import Awaitable, Callable

from pymodbus.client import AsyncModbusTcpClient
from pymodbus.exceptions import ModbusException, ModbusIOException
from pymodbus.pdu import ModbusPDU

from .registers import STATUS_LENGTH, RegisterMap, Space, StatusBlock, describe_range

CONNECT_ATTEMPTS = 2
CONNECT_TIMEOUT_S = 2.0
"""design.md § Python: connect timeout 2 s (per attempt)."""
CONNECT_BUDGET_S = 3.0
"""CHK-01: the TCP connect has a budget of ≤ 3 s including the tool's own retry; the retry gets what is left."""


def connect_timeout(elapsed_s: float) -> float:
    """The timeout of the next connect attempt: 2 s, capped so both attempts together stay within 3 s."""
    return max(0.0, min(CONNECT_TIMEOUT_S, CONNECT_BUDGET_S - elapsed_s))


REQUEST_TIMEOUT_S = 0.5
"""design.md § Python: request timeout 0.5 s (the protocol's ack budget; no single request may take longer)."""

log = logging.getLogger(__name__)


class PlcError(Exception):
    """A Modbus request failed: no connection, timeout, or a Modbus exception response (Transport)."""

    def __init__(self, message: str, cause: str | None = None) -> None:
        super().__init__(message)
        self.cause = cause
        """The cause in plain words ("Connection refused", "timed out (no answer within 500 ms)"), for a message
        that reports the failure after the one retry (review #49)."""


PROTOCOL_EXCEPTIONS = {1: "illegal function", 2: "illegal data address", 3: "illegal data value"}
"""protocol.md § Errors and debugging (ADR-37): Modbus exceptions 01, 02 and 03 mean the PLC answered but does not
serve the map as this document says: Protocol/ProtocolMismatch, never retried. Every other code (04 slave device
failure, 06 busy, 0A/0B gateway, …) stays Transport with the one retry."""

TRANSPORT_EXCEPTIONS = {4: "slave device failure", 6: "slave device busy", 10: "gateway path unavailable"}
TRANSPORT_EXCEPTIONS[11] = "gateway target device failed to respond"

_BLOCK_IN_SPACE = {
    Space.HOLDING: "the command block as holding registers",
    Space.INPUT: "the status block as input registers",
}


class PlcRefusedError(PlcError):
    """The PLC answered a request with Modbus exception 01, 02 or 03: Protocol/ProtocolMismatch (ADR-37)."""

    def __init__(self, operation: str, space: Space, code: int) -> None:
        self.code = code
        text = f"{operation} refused: Modbus exception {code:02X} ({PROTOCOL_EXCEPTIONS[code]})"
        if code in (1, 2):
            text += f" — the PLC does not serve {_BLOCK_IN_SPACE[space]}"
        super().__init__(text)


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


async def _open(client: AsyncModbusTcpClient) -> None:
    """``AsyncModbusTcpClient.connect()`` without its swallowing: pymodbus catches the connect exception, logs it and
    returns False, so the message would lose "Connection refused" (protocol.md § Errors and debugging, rule 1; review
    #2). This is its body (pymodbus 3.15.0, pinned) with the exception left to the caller."""
    ctx = client.ctx
    ctx.reset_delay()
    ctx.is_closing = False
    ctx.transport, _protocol = await ctx.call_create()


_PYMODBUS_PREFIX = re.compile(r"^Modbus Error: \[[^\]]*\]\s*")


def _reason(exc: BaseException) -> str:
    """The cause in plain words (rule 1; review #49): the OS's words for an errno ("Connection refused"), "timed out"
    for a request without an answer, never pymodbus's "No response received after 0 retries" (the tool's own retry is
    the one that counts)."""
    if isinstance(exc, PlcError) and exc.cause is not None:
        return exc.cause
    if isinstance(exc, TimeoutError):
        return "timed out"
    if isinstance(exc, OSError) and exc.errno is not None:
        return os.strerror(exc.errno)
    text = str(exc)
    if isinstance(exc, ModbusIOException) and "No response received" in text:
        return f"timed out (no answer within {round(REQUEST_TIMEOUT_S * 1000)} ms)"
    return _PYMODBUS_PREFIX.sub("", text).rstrip(". ") or type(exc).__name__


async def _complete_on_the_wire(pending: Awaitable[ModbusPDU]) -> ModbusPDU:
    """Await a request so that a cancellation (Ctrl-C) takes effect only once its answer arrived or timed out (≤ the
    0.5 s request timeout). Cancelling pymodbus mid-request left the answer to arrive later on the shared connection,
    where it spoiled the next requests (the beat's among them): they timed out, reconnected, and the stalled beat
    tripped the PLC watchdog during cleanup (GA-I-38 red under taskset -c 0,1: FaultCode 4)."""
    inner = asyncio.ensure_future(pending)
    try:
        return await asyncio.shield(inner)
    except asyncio.CancelledError:
        with contextlib.suppress(Exception):
            await asyncio.wait({inner})
        raise


class PlcClient:
    """Register access to one unit (protocol.md § Transport, "Register type"; ADR-36): the command block is holding
    registers (FC03 reads, FC06 for one register, FC16 for several), the status block input registers (FC04 reads)."""

    def __init__(self, host: str, port: int, unit: int, registers: RegisterMap | None = None) -> None:
        self.host = host
        self.port = port
        self.unit = unit
        self.registers = registers or RegisterMap()
        """Only for naming register ranges in error messages (protocol.md § Errors and debugging, rule 1)."""
        self._client: AsyncModbusTcpClient | None = None
        self.last_read: dict[int, int] = {}
        """The last value read from each holding register: the fallback ``lastRead`` evidence when a fresh read fails."""
        self.last_input: dict[int, int] = {}
        """The same for each input register. A separate map: holding 0 and input 0 are different registers."""
        self.retries = 0
        """Reconnect-and-retries performed so far; each check reports its own delta (§ Observed values)."""
        self._generation = 0
        self._reconnect_lock = asyncio.Lock()
        self.write_listener: Callable[[int, list[int]], None] | None = None
        """Called after every holding-register write that the PLC answered (one-verb mode prints command writes)."""
        self.status_listener: Callable[[StatusBlock], None] | None = None
        """Called with every status block read (one-verb mode prints each one, protocol.md "One-verb mode" step 5)."""
        self.guard: Callable[[], None] | None = None
        """Called before every request while a check runs: the runner sets the beat's ``raise_if_failed`` so every
        wait (a poll, a trip watch, a read after a sleep) reports a dead beat as Transport (review #7)."""

    def _operation(self, function: str, space: Space, address: int, count: int) -> str:
        """``FC04 read S+0…S+14 = input 0…14``: the function code and the register range in the protocol's rendering."""
        return f"{function} {describe_range(self.registers, space, address, count)}"

    def _where(self, function: str, space: Space, address: int, count: int) -> str:
        """``FC04 read S+0…S+14 = input 0…14 on host:port unit 1`` (protocol.md § Errors and debugging, rule 1: the
        endpoint, the operation and the register range)."""
        return f"{self._operation(function, space, address, count)} on {self.host}:{self.port} unit {self.unit}"

    @property
    def connected(self) -> bool:
        return self._client is not None and self._client.connected

    async def connect(self, attempts: int = CONNECT_ATTEMPTS) -> None:
        """Connect within CHK-01's budget. pymodbus uses one timeout for connect and for requests, so the client
        connects with the 2 s connect timeout and then switches to the 0.5 s request timeout. Automatic reconnection is
        off (``reconnect_delay=0``): a lost link is reported, never silently recovered (protocol.md § Errors and
        debugging, rule 3).

        The second attempt is the tool's one retry: logged at Warning and counted in ``retries`` like a request retry
        (§ Error class of a FAIL, "The one retry"; review #11). A reconnect after a failed request makes one attempt
        only, so the request's one retry is not doubled."""
        started = time.monotonic()
        last: BaseException | None = None
        for attempt in range(attempts):
            if attempt:
                log.warning("connect to %s:%d failed; retrying once", self.host, self.port)
                self.retries += 1
            timeout = connect_timeout(time.monotonic() - started)
            client = AsyncModbusTcpClient(self.host, port=self.port, timeout=timeout, retries=0, reconnect_delay=0)
            try:
                async with asyncio.timeout(timeout):
                    await _open(client)
            except (OSError, TimeoutError, ModbusException) as exc:
                last = exc
                client.close()
                continue
            # pymodbus's TransactionManager (``client.ctx``) waits for an answer on ITS OWN copy of the parameters
            # (``timeout_connect``); ``client.comm_params`` is not read after construction (review #19).
            client.ctx.comm_params.timeout_connect = REQUEST_TIMEOUT_S
            self._client = client
            self._generation += 1
            return
        cause = _reason(last) if last is not None else "no attempt"
        if attempts > 1:
            # Review #49 (C#'s words): two attempts, the second after a fresh connection.
            message = f"connect on {self.host}:{self.port} failed twice (reconnected once): {cause}"
        else:
            message = (
                f"connect to {self.host}:{self.port} failed (1 attempt in {time.monotonic() - started:.1f} s): {cause}"
            )
        raise PlcError(message, cause=cause)

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
            await self.connect(attempts=1)

    async def _transact(
        self,
        where: str,
        request: Callable[[AsyncModbusTcpClient], Awaitable[ModbusPDU]],
        *,
        retry: bool,
        operation: str,
        space: Space,
    ) -> ModbusPDU:
        """One request with the protocol's one reconnect-and-retry (§ Errors and debugging, rule 3; § Error class of
        a FAIL, "The one retry"), logged at Warning and counted in ``retries``. ``retry=False`` for a command write:
        "A command is never re-sent". A Modbus exception 01/02/03 is ``PlcRefusedError`` and never retried (ADR-37)."""
        if self.guard is not None:
            self.guard()
        retried = False
        while True:
            generation = self._generation
            client = self._require()
            try:
                response = await _complete_on_the_wire(request(client))
                if response.isError():
                    code = int(getattr(response, "exception_code", 0))
                    if code in PROTOCOL_EXCEPTIONS:
                        raise PlcRefusedError(operation, space, code)
                    name = TRANSPORT_EXCEPTIONS.get(code, "unexpected code")
                    cause = f"Modbus exception {code:02X} ({name})"
                    raise PlcError(f"{where} failed: {cause}", cause=cause)
                return response
            except PlcRefusedError:
                raise  # the PLC answered: Protocol, no reconnect-and-retry (ADR-37)
            except (OSError, ModbusException, PlcError) as exc:
                failure = exc if isinstance(exc, PlcError) else _failure(f"{where} failed", exc)
                if isinstance(failure, asyncio.CancelledError) or not retry:
                    raise failure from exc  # a single attempt reports as it is
                cause = _reason(exc)
                if retried:
                    # Review #49: the protocol's shape for the failure after the one retry.
                    raise PlcError(f"{where} failed twice (reconnected once): {cause}", cause=cause) from exc
                log.warning("%s failed: %s; reconnecting and retrying once", where, cause)
                self.retries += 1
                retried = True
                try:
                    await self._reconnect(generation)
                except PlcError as again:
                    # The reconnect is the second attempt's start: its failure is that attempt's cause.
                    raise PlcError(
                        f"{where} failed twice (reconnected once): {_reason(again)}", cause=_reason(again)
                    ) from exc

    async def read(self, address: int, count: int) -> list[int]:
        """FC03: holding registers (the command block)."""
        where = self._where("FC03 read", Space.HOLDING, address, count)
        response = await self._transact(
            where,
            lambda c: c.read_holding_registers(address, count=count, device_id=self.unit),
            retry=True,
            operation=self._operation("FC03 read", Space.HOLDING, address, count),
            space=Space.HOLDING,
        )
        return self._answered(where, response, count, self.last_read, address)

    async def read_input(self, address: int, count: int) -> list[int]:
        """FC04: input registers (the status block, ADR-36), with the same shield, timeout and one retry as ``read``."""
        where = self._where("FC04 read", Space.INPUT, address, count)
        response = await self._transact(
            where,
            lambda c: c.read_input_registers(address, count=count, device_id=self.unit),
            retry=True,
            operation=self._operation("FC04 read", Space.INPUT, address, count),
            space=Space.INPUT,
        )
        return self._answered(where, response, count, self.last_input, address)

    @staticmethod
    def _answered(where: str, response: ModbusPDU, count: int, last: dict[int, int], address: int) -> list[int]:
        registers = list(response.registers)
        if len(registers) != count:
            raise PlcError(f"{where} failed: answered {len(registers)} registers")
        for offset, value in enumerate(registers):
            last[address + offset] = value
        return registers

    async def write(self, address: int, values: list[int], *, retry: bool = True) -> None:
        function = "FC06" if len(values) == 1 else "FC16"
        where = f"{self._where(f'{function} write', Space.HOLDING, address, len(values))} = {values}"
        operation = f"{self._operation(f'{function} write', Space.HOLDING, address, len(values))} = {values}"
        if len(values) == 1:
            await self._transact(
                where,
                lambda c: c.write_register(address, values[0], device_id=self.unit),
                retry=retry,
                operation=operation,
                space=Space.HOLDING,
            )
        else:
            await self._transact(
                where,
                lambda c: c.write_registers(address, values, device_id=self.unit),
                retry=retry,
                operation=operation,
                space=Space.HOLDING,
            )
        log.debug("wrote %d = %s", address, values)
        if self.write_listener is not None:
            self.write_listener(address, values)

    async def read_status(self, registers: RegisterMap) -> StatusBlock:
        """One FC04 of S+0…S+14 (protocol.md § Transport, "Consistency")."""
        status = StatusBlock.parse(await self.read_input(registers.status, STATUS_LENGTH))
        if self.status_listener is not None:
            self.status_listener(status)
        return status
