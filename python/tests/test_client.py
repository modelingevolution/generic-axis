"""``PlcClient`` failure mapping."""

from __future__ import annotations

import asyncio

import pytest

from generic_axis_check.client import PlcClient, PlcError

from .stub_plc import StubPlc


async def test_read_from_a_silent_unit_raises_plc_error(stub: StubPlc) -> None:
    client = PlcClient("127.0.0.1", stub.port, 9)  # the stub stays silent for a unit it does not serve
    await client.connect()
    try:
        with pytest.raises(PlcError, match="FC03 100"):
            await client.read(100, 15)
    finally:
        client.close()


async def test_cancelling_a_request_in_flight_raises_cancelled_error_not_plc_error(stub: StubPlc) -> None:
    # pymodbus turns a cancellation into ModbusIOException; Ctrl-C must still stop the run (GA-I-38).
    client = PlcClient("127.0.0.1", stub.port, 9)
    await client.connect()
    try:
        task = asyncio.create_task(client.read(100, 15))
        await asyncio.sleep(0.1)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
    finally:
        client.close()


async def test_connect_to_a_closed_port_raises_plc_error() -> None:
    async with StubPlc() as plc:
        port = plc.port
    with pytest.raises(PlcError, match="connect"):
        await PlcClient("127.0.0.1", port, 1).connect()
