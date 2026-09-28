"""Runner behaviour against the in-process stub PLC: GA-U-63.py (prerequisites) plus pre-flight, fault detection,
restore and cleanup. The stub is not the acceptance target; the C# simulator is (tests/test_integration.py)."""

from __future__ import annotations

import asyncio
import dataclasses
import re

import pytest

from generic_axis_check.beat import Beater
from generic_axis_check.checks import CHECKS, FAIL, PASS, SKIPPED, Check, Outcome
from generic_axis_check.client import PlcClient
from generic_axis_check.context import CheckContext, Options
from generic_axis_check.registers import RegisterMap
from generic_axis_check.runner import Report, run

from .stub_plc import StubPlc

MAP = RegisterMap()


def by_id(report: Report) -> dict[str, tuple[str, str]]:
    return {c.id: (c.result, c.message) for c in report.checks}


def upto(check_id: str) -> tuple[Check, ...]:
    return tuple(c for c in CHECKS if c.id <= check_id)


async def _pass(_ctx: CheckContext) -> Outcome:
    return Outcome(PASS, "fake")


FAKE = tuple(dataclasses.replace(c, run=_pass) for c in CHECKS)


def options(plc: StubPlc, *, motion: bool = False) -> Options:
    return Options(host="127.0.0.1", port=plc.port, allow_motion=motion)


async def test_run_failed_map_version_skips_every_dependant_and_writes_nothing() -> None:
    async with StubPlc() as plc:
        plc.o.map_version = 2
        report = await run(options(plc, motion=True))
    results = by_id(report)
    assert results["CHK-01"][0] == PASS
    assert results["CHK-02"] == (
        FAIL,
        "Protocol/ProtocolMismatch: MapVersion not 1. Read MapVersion (S+14 = 114) = 2, expected 1.",
    )
    chk02 = report.checks[1]
    assert chk02.error_class == "Protocol"
    assert chk02.last_read is not None
    assert chk02.last_read.status[14] == 2
    assert len(chk02.last_read.command) == 12
    assert all(results[f"CHK-{n:02d}"][0] == SKIPPED for n in range(3, 17))
    assert results["CHK-03"][1] == "needs CHK-02, which FAILED"
    assert results["CHK-07"][1] == "needs CHK-06, which SKIPPED"
    assert report.exit_code == 1
    assert plc.writes == []
    assert report.cleanup == []


async def test_run_without_motion_skips_chk12_to_16_with_needs_allow_motion(stub: StubPlc) -> None:
    report = await run(options(stub), checks=FAKE)
    results = by_id(report)
    assert all(results[f"CHK-{n:02d}"][0] == PASS for n in range(1, 12))
    assert all(results[f"CHK-{n:02d}"] == (SKIPPED, "needs --allow-motion") for n in range(12, 17))
    assert report.exit_code == 0


async def test_run_refuses_a_live_foreign_commander_with_exit_3_and_writes_nothing(stub: StubPlc) -> None:
    commander = PlcClient("127.0.0.1", stub.port, 1)
    await commander.connect()
    await commander.write(MAP.lease_owner, [1])
    beat = Beater(commander, MAP)
    await beat.start()
    try:
        before = len(stub.writes)
        report = await run(options(stub, motion=True))
        foreign = [w for w in stub.writes[before:] if w[0] != MAP.heartbeat]
    finally:
        await beat.stop()
        commander.close()
    assert report.refused
    assert report.exit_code == 3
    assert all(c.result == SKIPPED and "LeaseOwner 1 is beating" in c.message for c in report.checks)
    assert foreign == []
    assert stub.regs[MAP.lease_owner] == 1
    assert report.cleanup == []


async def test_run_takes_over_a_dead_foreign_lease(stub: StubPlc) -> None:
    stub.regs[MAP.lease_owner] = 1  # a dead commander: owner set, beat never changes
    report = await run(options(stub), checks=upto("CHK-07"))
    assert not report.refused
    assert by_id(report)["CHK-06"][0] == PASS
    assert stub.regs[MAP.lease_owner] == 0  # released by cleanup


async def test_run_catches_a_plc_that_never_acknowledges() -> None:
    async with StubPlc() as plc:
        plc.o.suppress_ack = True
        report = await run(options(plc), checks=upto("CHK-08"))
    results = by_id(report)
    assert results["CHK-06"][0] == FAIL
    assert re.fullmatch(
        r"Protocol/NotAcknowledged: Enable 1 not accepted\. CommandSeq 1 written, CommandAck 0 read after \d+ ms, "
        r"State 0 read\.",
        results["CHK-06"][1],
    )
    assert report.checks[5].observed == {"commandSeq": 1, "commandAck": 0, "state": 0}
    assert results["CHK-07"] == (SKIPPED, "needs CHK-06, which FAILED")
    assert results["CHK-08"] == (SKIPPED, "needs CHK-06, which FAILED")


async def test_run_catches_a_plc_without_the_watchdog() -> None:
    async with StubPlc() as plc:
        plc.o.watchdog_disabled = True
        report = await run(options(plc), checks=upto("CHK-10"))
    results = by_id(report)
    assert results["CHK-08"][0] == FAIL
    assert results["CHK-08"][1].startswith(
        "Protocol/ProtocolMismatch: stalled beat: no trip within 1.5 s of the last beat. Read State (S+0 = 100) = 0, "
    )
    assert results["CHK-09"][0] == SKIPPED
    assert results["CHK-10"][0] == SKIPPED


async def test_run_catches_swapped_word_order_in_chk03() -> None:
    async with StubPlc() as plc:
        plc.o.swapped_word_order = True
        report = await run(options(plc), checks=upto("CHK-03"))
    chk03 = report.checks[2]
    assert chk03.result == FAIL
    assert chk03.message.startswith("Protocol/ProtocolMismatch: limits not sane: TravelMin is not < TravelMax")
    assert "Read TravelMin (S+8 = 108) = 0, TravelMax (S+10 = 110) = " in chk03.message
    assert chk03.observed["travelMax"] < 0


async def test_run_catches_unpublished_limits_in_chk03() -> None:
    async with StubPlc() as plc:
        plc.o.publish_limits = False
        report = await run(options(plc), checks=upto("CHK-03"))
    chk03 = report.checks[2]
    assert chk03.result == FAIL
    assert chk03.message == (
        "Protocol/ProtocolMismatch: limits not published (all zero). Read TravelMin (S+8 = 108) = 0, "
        "TravelMax (S+10 = 110) = 0, MaxVelocity (S+12 = 112) = 0."
    )


@pytest.mark.timeout(120)
async def test_run_full_checklist_with_motion_passes_and_leaves_the_axis_clean(stub: StubPlc) -> None:
    report = await run(options(stub, motion=True))
    assert [(c.id, c.result, c.message) for c in report.checks if c.result != PASS] == []
    assert report.exit_code == 0
    observed = {c.id: c.observed for c in report.checks}
    assert 1000 <= observed["CHK-08"]["tripAfterMs"] <= 1500
    assert observed["CHK-14"]["haltMs"] <= 200
    await asyncio.sleep(0.05)
    assert stub.axis.state == 0
    assert stub.regs[MAP.lease_owner] == 0
    assert stub.regs[MAP.watchdog_fault] == 0


@pytest.mark.timeout(120)
async def test_run_interrupted_mid_move_stops_the_axis_and_journals_the_cleanup(stub: StubPlc) -> None:
    task = asyncio.create_task(run(options(stub, motion=True)))
    while stub.axis.state != 3:  # noqa: ASYNC110 — polls the stub's scan state; there is no event to await
        await asyncio.sleep(0.005)
    task.cancel()
    report = await task
    assert report.interrupted
    assert report.exit_code == 1
    assert by_id(report)["CHK-13"][1] == "Commander/Cancelled: interrupted (Ctrl-C) during this check."
    assert report.checks[12].error_class == "Commander"
    assert by_id(report)["CHK-16"] == (SKIPPED, "interrupted")
    await asyncio.sleep(0.2)
    assert stub.axis.state in (0, 1)
    assert stub.axis.v == 0
    assert stub.regs[MAP.lease_owner] == 0
    assert any("Stop" in entry for entry in report.cleanup)
    assert "C+9 = 0 (release lease)" in report.cleanup
