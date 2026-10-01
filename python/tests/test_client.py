"""``PlcClient`` failure mapping."""

from __future__ import annotations

import asyncio
import contextlib
import logging
import re
import time

import pytest

from generic_axis_check.client import REQUEST_TIMEOUT_S, PlcClient, PlcError, PlcRefusedError, connect_timeout

from .stub_plc import StubOptions, StubPlc


async def test_read_from_a_silent_unit_raises_plc_error(stub: StubPlc) -> None:
    client = PlcClient("127.0.0.1", stub.port, 9)  # the stub stays silent for a unit it does not serve
    await client.connect()
    try:
        with pytest.raises(PlcError, match=r"FC03 read C\+0…C\+11 = holding 0…11 on 127\.0\.0\.1:\d+ unit 9 failed: "):
            await client.read(0, 12)
    finally:
        client.close()


async def test_cancelling_a_request_in_flight_raises_cancelled_error_not_plc_error(stub: StubPlc) -> None:
    # pymodbus turns a cancellation into ModbusIOException; Ctrl-C must still stop the run (GA-I-38).
    client = PlcClient("127.0.0.1", stub.port, 9)
    await client.connect()
    try:
        task = asyncio.create_task(client.read_input(0, 15))
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
            await client.read_input(0, 15)
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
            assert await client.read(9, 1) == [0]
        assert client.retries == 1
        warnings = [r.getMessage() for r in caplog.records if r.name == "generic_axis_check.client"]
        assert len(warnings) == 1
        assert warnings[0].startswith("FC03 read C+9 = holding 9 on 127.0.0.1:")
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
        with pytest.raises(PlcError, match=r"FC16 write C\+0…C\+1 = holding 0…1"):
            await client.write(0, [1, 7], retry=False)
        assert stub.requests - before == 1
        assert client.retries == 0
    finally:
        client.close()


async def test_a_cancel_during_a_request_leaves_the_next_request_answered_at_once(stub: StubPlc) -> None:
    # GA-U-132.py (review #32): a cancellation never interrupts a frame in flight. The read on the wire (its reply
    # held 200 ms) completes before the cancellation takes effect, so its answer cannot land on the next request: that
    # one answers at once, with no retry. Cancelling mid-frame left the late answer on the connection, and the next
    # request waited behind it (pymodbus: "transaction_id=2 but got id=1, Skipping"); on a loaded host, past 0.5 s.
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        stub.reply_delay_if = lambda pdu: 0.2 if pdu[0] == 3 and int.from_bytes(pdu[1:3]) == 0 else 0.0
        in_flight = asyncio.create_task(client.read(0, 12))
        while not stub.replying_late:  # noqa: ASYNC110 — waits for the stub to hold the reply; no event to await
            await asyncio.sleep(0.001)
        cancelled_at = time.monotonic()
        in_flight.cancel()
        with pytest.raises(asyncio.CancelledError):
            await in_flight
        cancel_s = time.monotonic() - cancelled_at
        stub.reply_delay_if = None
        started = time.monotonic()
        assert await client.read_input(14, 1) == [1]  # MapVersion
        next_read_s = time.monotonic() - started
    finally:
        client.close()
    assert next_read_s < 0.05, next_read_s
    assert client.retries == 0
    assert 0.15 <= cancel_s <= 0.5, cancel_s  # bounded by the frame's own answer or its 0.5 s timeout


# --- GA-U-134.py (ADR-36): the status block is input registers, read by FC04 with the same transport rules ---


async def test_read_input_reads_the_input_space_not_the_holding_space(stub: StubPlc) -> None:
    # Holding 0…14 and input 0…14 are different registers: FC04 answers the input array, FC03 the holding one, and
    # the fallback evidence of each is kept apart.
    stub.regs[12:15] = (312, 313, 314)  # holding 12…14: past the command block, where S+12…S+14 sit in the input space
    published = list(stub.inputs[0:15])  # an axis at rest: the scan republishes the same block
    assert published[14] == 1
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        assert await client.read_input(0, 15) == published
        assert await client.read(0, 15) == stub.regs[0:15]
    finally:
        client.close()
    assert stub.regs[12:15] == [312, 313, 314]
    assert client.last_input == dict(enumerate(published))
    assert client.last_read == dict(enumerate(stub.regs[0:15]))


async def test_read_input_from_a_silent_unit_names_fc04_and_the_input_range(stub: StubPlc) -> None:
    client = PlcClient("127.0.0.1", stub.port, 9)
    await client.connect()
    started = time.monotonic()
    try:
        with pytest.raises(PlcError, match=r"FC04 read S\+0…S\+14 = input 0…14 on 127\.0\.0\.1:\d+ unit 9 failed: "):
            await client.read_input(0, 15)
    finally:
        client.close()
    elapsed = time.monotonic() - started
    assert client.retries == 1  # the one reconnect-and-retry, then Transport
    assert 0.9 <= elapsed <= 1.6, elapsed  # two 0.5 s request timeouts (review #19)


async def test_read_input_retries_a_lost_answer_once_at_warning_and_counts_it(
    stub: StubPlc, caplog: pytest.LogCaptureFixture
) -> None:
    stub.inputs[14] = 1
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        stub.drop_next = 1
        with caplog.at_level(logging.WARNING, logger="generic_axis_check.client"):
            assert await client.read_input(14, 1) == [1]
    finally:
        client.close()
    assert client.retries == 1
    warnings = [r.getMessage() for r in caplog.records if r.name == "generic_axis_check.client"]
    assert len(warnings) == 1
    assert warnings[0].startswith("FC04 read S+14 = input 14 on 127.0.0.1:")
    assert warnings[0].endswith("; reconnecting and retrying once")


async def test_read_input_cancelled_in_flight_leaves_the_next_request_answered_at_once(stub: StubPlc) -> None:
    # The #32 shield on FC04: the held reply completes before the cancellation takes effect.
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        stub.reply_delay_if = lambda pdu: 0.2 if pdu[0] == 4 else 0.0
        in_flight = asyncio.create_task(client.read_input(0, 15))
        async with asyncio.timeout(2):  # an FC04 the stub never holds fails here, not at the suite's timeout
            while not stub.replying_late:  # noqa: ASYNC110 — waits for the stub to hold the reply; no event to await
                await asyncio.sleep(0.001)
        in_flight.cancel()
        with pytest.raises(asyncio.CancelledError):
            await in_flight
        stub.reply_delay_if = None
        started = time.monotonic()
        await client.read_input(0, 15)
        next_read_s = time.monotonic() - started
    finally:
        client.close()
    assert next_read_s < 0.05, next_read_s
    assert client.retries == 0


async def test_a_holding_write_at_0_to_14_never_changes_the_input_status_block() -> None:
    # GA-U-136.py (ADR-36): holding 0…14 and input 0…14 are separate registers. With the scan held (no republish), an
    # FC16 over holding 0…14 lands in the command block and holding 12…14, and FC04 still answers the status block the
    # PLC published. A PLC serving the status block from its holding array (the old map at base 0) fails here.
    async with StubPlc(StubOptions(scan_s=60.0)) as plc:
        published = list(plc.inputs[0:15])
        assert published[14] == 1
        client = PlcClient("127.0.0.1", plc.port, 1)
        await client.connect()
        try:
            words = [0, 0, *([0xBEEF] * 6), 0xBEEF, 0, 0xBEEF, 0, 0xBEEF, 0xBEEF, 0xBEEF]  # no edge, no lease
            await client.write(0, words)
            assert await client.read(0, 15) == words
            assert await client.read_input(0, 15) == published
        finally:
            client.close()


# --- GA-U-138.py (ADR-37, #54): Modbus exceptions 01/02/03 are Protocol, never retried; others stay Transport ---


@pytest.mark.parametrize(
    ("code", "text"),
    [
        (1, "01 (illegal function) — the PLC does not serve the status block as input registers"),
        (2, "02 (illegal data address) — the PLC does not serve the status block as input registers"),
        (3, "03 (illegal data value)"),
    ],
)
async def test_an_fc04_refused_with_01_02_03_is_a_protocol_refusal_sent_once(
    stub: StubPlc, code: int, text: str
) -> None:
    stub.exception_if = lambda pdu: code if pdu[0] == 4 else None
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    before = stub.requests
    try:
        with pytest.raises(PlcRefusedError) as refused:
            await client.read_input(0, 15)
    finally:
        client.close()
    assert str(refused.value) == f"FC04 read S+0…S+14 = input 0…14 refused: Modbus exception {text}"
    assert refused.value.code == code
    assert (stub.requests - before, client.retries) == (1, 0)  # no reconnect-and-retry


async def test_a_holding_read_refused_with_02_names_the_command_block(stub: StubPlc) -> None:
    stub.exception_if = lambda pdu: 2 if pdu[0] == 3 else None
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    try:
        with pytest.raises(
            PlcRefusedError, match=r"^FC03 read C\+8…C\+10 = holding 8…10 refused: Modbus exception 02 "
        ):
            await client.read(8, 3)
    finally:
        client.close()
    assert client.retries == 0


@pytest.mark.parametrize(("code", "name"), [(4, "slave device failure"), (6, "slave device busy"), (11, "gateway")])
async def test_other_modbus_exceptions_stay_transport_with_the_one_retry(stub: StubPlc, code: int, name: str) -> None:
    stub.exception_if = lambda pdu: code if pdu[0] == 4 else None
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    before = stub.requests
    try:
        with pytest.raises(PlcError) as failure:
            await client.read_input(0, 15)
    finally:
        client.close()
    assert not isinstance(failure.value, PlcRefusedError)
    assert f"Modbus exception {code:02X} ({name}" in str(failure.value)
    assert (stub.requests - before, client.retries) == (2, 1)
