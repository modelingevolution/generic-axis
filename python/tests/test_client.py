"""``PlcClient`` failure mapping."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import re
import time

import pytest

from generic_axis_check.client import REQUEST_TIMEOUT_S, PlcClient, PlcError, connect_timeout

from .stub_plc import StubPlc


async def test_read_from_a_silent_unit_raises_plc_error(stub: StubPlc) -> None:
    client = PlcClient("127.0.0.1", stub.port, 9)  # the stub stays silent for a unit it does not serve
    await client.connect()
    try:
        with pytest.raises(PlcError, match=r"read S\+0…S\+14 \(100…114\) on 127\.0\.0\.1:\d+ unit 9 failed: "):
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


async def test_connect_to_a_closed_port_retries_once_at_warning_and_counts_it(caplog: pytest.LogCaptureFixture) -> None:
    # GA-U-103.py (review #11): the connect retry is the one retry: one Warning, counted in ``retries``.
    async with StubPlc() as plc:
        port = plc.port
    client = PlcClient("127.0.0.1", port, 1)
    with caplog.at_level(logging.WARNING, logger="generic_axis_check.client"), pytest.raises(PlcError, match="connect"):
        await client.connect()
    assert client.retries == 1
    assert [r.getMessage() for r in caplog.records if r.name == "generic_axis_check.client"] == [
        f"connect to 127.0.0.1:{port} failed; retrying once"
    ]


async def test_read_from_a_silent_server_fails_after_two_half_second_attempts() -> None:
    # GA-U-102.py (review #19): "a request got no answer within 500 ms" is Transport. One attempt, the one reconnect,
    # one more attempt: ≈ 2 × 0.5 s, never pymodbus's 1.5 s per attempt.
    async def never_answer(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        with contextlib.suppress(ConnectionError):
            while await reader.read(256):
                pass
        writer.close()

    server = await asyncio.start_server(never_answer, "127.0.0.1", 0)
    port = server.sockets[0].getsockname()[1]
    client = PlcClient("127.0.0.1", port, 1)
    try:
        await client.connect()
        started = time.monotonic()
        with pytest.raises(PlcError, match="failed"):
            await client.read(100, 15)
        elapsed = time.monotonic() - started
    finally:
        client.close()
        server.close()
        await server.wait_closed()
    assert client.retries == 1
    assert 0.9 <= elapsed <= 1.6, elapsed


def test_connect_timeout_is_2_s_and_both_attempts_fit_chk01s_3_s_budget() -> None:
    # Review #19 / design.md: connect timeout 2 s, distinct from the 0.5 s request timeout; CHK-01 budget ≤ 3 s.
    assert connect_timeout(0.0) == 2.0
    assert connect_timeout(2.0) == 1.0
    assert connect_timeout(0.1) == 2.0
    assert connect_timeout(3.5) == 0.0
    assert REQUEST_TIMEOUT_S == 0.5


async def test_connect_failure_message_carries_the_exception_text() -> None:
    # GA-U-104.py (review #2 b): rule 1, a transport error names the exception ("Connection refused").
    async with StubPlc() as plc:
        port = plc.port
    with pytest.raises(PlcError) as failure:
        await PlcClient("127.0.0.1", port, 1).connect()
    assert re.fullmatch(
        rf"connect to 127\.0\.0\.1:{port} failed \(2 attempts in \d+\.\d s\): Connection refused \(.+\)",
        str(failure.value),
    ), str(failure.value)


async def test_a_lost_answer_is_retried_once_at_warning_and_counted(
    stub: StubPlc, caplog: pytest.LogCaptureFixture
) -> None:
    # GA-U-109.py (review #21 mutant 1): "The one retry … logs it at Warning, and counts it in that check's retries".
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        stub.drop_next = 1
        with caplog.at_level(logging.WARNING, logger="generic_axis_check.client"):
            assert await client.read(114, 1) == [1]
        assert client.retries == 1
        warnings = [r.getMessage() for r in caplog.records if r.name == "generic_axis_check.client"]
        assert len(warnings) == 1
        assert warnings[0].startswith("read S+14 (114) on 127.0.0.1:")
        assert warnings[0].endswith("; reconnecting and retrying once")
    finally:
        client.close()


async def test_a_command_write_is_never_re_sent(stub: StubPlc) -> None:
    # GA-U-109.py (review #21 mutant 1): "A command is never re-sent": one attempt, then Transport, no retry counted.
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        stub.drop_next = 1
        before = stub.requests
        with pytest.raises(PlcError, match=r"write C\+0…C\+1"):
            await client.write(0, [1, 7], retry=False)
        assert stub.requests - before == 1
        assert client.retries == 0
    finally:
        client.close()
