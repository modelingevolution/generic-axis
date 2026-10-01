"""``--dump [--watch]`` (protocol.md § Errors and debugging, rule 4)."""

from __future__ import annotations

import asyncio

import pytest

from generic_axis_check.__main__ import main, run_dump
from generic_axis_check.client import PlcClient
from generic_axis_check.context import Options
from generic_axis_check.dump import dump, render
from generic_axis_check.registers import RegisterMap, to_words

from .stub_plc import StubPlc

MAP = RegisterMap()


def test_render_decodes_every_register_by_name() -> None:
    command = [1 | 2, 7, *to_words(10_000), *to_words(-50_000), 0, 0, 12, 65535, 1, 2]
    status = [7, 1 | 32 | 256, *to_words(1234), 0, 0, 4, 6, 0, 0, 0x9680, 0x98, 0xA120, 7, 1]
    rows = render(MAP, command, status).splitlines()
    assert "C+0    holding 0     Command                0x0003  Enable | Home" in rows
    assert "C+4    holding 4     Velocity               0x3CB0  -50000 = -50.000 u/s" in rows
    assert "C+9    holding 9     LeaseOwner             0xFFFF  65535" in rows
    assert "C+10   holding 10    WatchdogFault          0x0001  1 tripped" in rows
    assert "S+0    input 0       State                  0x0007  7 ErrorStop" in rows
    assert "S+1    input 1       Flags                  0x0121  Homed | DriveReady | unknown bits 0x0100" in rows
    assert "S+6    input 6       FaultCode              0x0004  4 watchdog" in rows
    assert "S+10   input 10      TravelMax              0x9680  10000000 = 10000.000 u" in rows
    assert len([r for r in rows if r[:1] in ("C", "S") and "+" in r[:3]]) == 27


def test_render_prints_two_tables_with_typed_absolute_addresses() -> None:
    # GA-U-135.py (ADR-36, rule 4): "holding C+0…C+11 and input S+0…S+14" — one table per space, each titled with its
    # function codes and base, each row's absolute address typed; both blocks at 0 stay told apart.
    rows = render(RegisterMap(200, 300), list(range(12)), list(range(15))).splitlines()
    command_title = rows.index("Command block: holding registers (FC03 read, FC06/FC16 write), C = 200")
    status_title = rows.index("Status block: input registers (FC04 read), S = 300")
    command_rows = [r for r in rows[command_title:status_title] if r.startswith("C+")]
    status_rows = [r for r in rows[status_title:] if r.startswith("S+")]
    assert [r.split()[:3] for r in command_rows] == [[f"C+{n}", "holding", str(200 + n)] for n in range(12)]
    assert [r.split()[:3] for r in status_rows] == [[f"S+{n}", "input", str(300 + n)] for n in range(15)]


def test_render_names_an_invalid_state_as_the_protocol_class_not_an_interpretation() -> None:
    # GA-U-69.py + review #2 d: an invalid State is Protocol/ProtocolMismatch; the dump never claims "ErrorStop".
    rows = render(MAP, [0] * 12, [5, *([0] * 13), 1]).splitlines()
    assert "S+0    input 0       State                  0x0005  5 (invalid: Protocol/ProtocolMismatch)" in rows


async def test_dump_reads_both_blocks_and_writes_nothing(stub: StubPlc) -> None:
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    printed: list[str] = []
    try:
        await dump(client, MAP, printed.append)
    finally:
        client.close()
    assert len(printed) == 1
    assert "S+14   input 14      MapVersion             0x0001  1" in printed[0]
    assert stub.writes == []


async def test_dump_watch_repeats_at_5_hz_until_cancelled(stub: StubPlc) -> None:
    printed: list[str] = []
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    task = asyncio.create_task(dump(client, MAP, printed.append, watch=True))
    await asyncio.sleep(1.1)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
    client.close()
    assert 5 <= len(printed) <= 7
    assert stub.writes == []


async def test_run_dump_exits_1_on_a_transport_error(capsys: pytest.CaptureFixture[str]) -> None:
    async with StubPlc() as plc:
        port = plc.port
    assert await run_dump(Options(host="127.0.0.1", port=port), watch=False) == 1
    assert capsys.readouterr().err.startswith(f"Transport/CommunicationLost: connect to 127.0.0.1:{port}")


async def test_run_dump_exits_0_when_both_blocks_were_read(stub: StubPlc) -> None:
    assert await run_dump(Options(host="127.0.0.1", port=stub.port), watch=False) == 0


@pytest.mark.parametrize("argv", [["plc", "--watch"], ["plc", "--dump", "--report", "r.md"]])
def test_dump_usage_errors_exit_2(argv: list[str]) -> None:
    assert main(argv) == 2
