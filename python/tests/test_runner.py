"""Runner behaviour against the in-process stub PLC: GA-U-63.py (prerequisites) plus pre-flight, fault detection,
restore and cleanup. The stub is not the acceptance target; the C# simulator is (tests/test_integration.py)."""

from __future__ import annotations

import asyncio
import dataclasses
import re

import pytest

from generic_axis_check.beat import Beater
from generic_axis_check.checks import CHECKS, FAIL, OBSERVED, PASS, SKIPPED, Check, Outcome
from generic_axis_check.client import PlcClient
from generic_axis_check.context import CheckContext, Options
from generic_axis_check.registers import Command, RegisterMap
from generic_axis_check.runner import Report, run

from .stub_plc import StubPlc

MAP = RegisterMap()


def by_id(report: Report) -> dict[str, tuple[str, str]]:
    return {c.id: (c.result, c.message) for c in report.checks}


def num(observed: dict[str, int | None], key: str) -> int:
    value = observed[key]
    assert value is not None, key
    return value


def upto(check_id: str) -> tuple[Check, ...]:
    return tuple(c for c in CHECKS if c.id <= check_id)


async def _pass(_ctx: CheckContext) -> Outcome:
    return Outcome(PASS, "fake")


FAKE = tuple(dataclasses.replace(c, run=_pass) for c in CHECKS)
TO_THE_FIRST_MOVE = tuple(c for c in CHECKS if c.id in {"CHK-01", "CHK-02", "CHK-03", "CHK-06", "CHK-12", "CHK-13"})
"""The shortest run that reaches CHK-13's MoveAbsolute (State 3)."""


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
    message = report.checks[0].message
    assert re.fullmatch(
        r"pre-flight: another commander is live: Heartbeat \(C\+8\) changed \d+( → \d+)+ within 1 s, "
        r"LeaseOwner \(C\+9\) 1; stop it first",
        message,
    )
    assert all(c.result == SKIPPED and c.message == message for c in report.checks)
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
    assert report.checks[5].observed == {
        "enableAckMs": None,
        "enableStateMs": None,
        "disableAckMs": None,
        "disableStateMs": None,
        "retries": 0,
    }
    assert report.unknown_observed == set()
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
    assert num(chk03.observed, "travelMax") < 0


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
    assert report.unknown_observed == set()
    for c in report.checks:
        assert list(c.observed) == [*OBSERVED[c.id], "retries"]
        assert all(v is not None for v in c.observed.values()), (c.id, c.observed)
    observed = {c.id: c.observed for c in report.checks}
    assert 1000 <= num(observed["CHK-08"], "tripAfterMs") <= 1500
    assert num(observed["CHK-14"], "haltMs") <= 200
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
    assert report.exit_code == 4
    assert report.result == "INTERRUPTED"
    assert by_id(report)["CHK-13"] == (SKIPPED, "interrupted by the operator during CHK-13")
    assert report.checks[12].observed == {}
    assert by_id(report)["CHK-16"] == (SKIPPED, "interrupted by the operator during CHK-13")
    await asyncio.sleep(0.2)
    assert stub.axis.state in (0, 1)
    assert stub.axis.v == 0
    assert stub.regs[MAP.lease_owner] == 0
    assert any("Stop" in entry for entry in report.cleanup)
    assert "C+9 = 0 (release lease)" in report.cleanup


async def test_run_refuses_a_second_tool_beating_under_the_checkers_own_id(stub: StubPlc) -> None:
    # protocol.md "Pre-flight": any beat change refuses, even with LeaseOwner = the tool's own id.
    other = PlcClient("127.0.0.1", stub.port, 1)
    await other.connect()
    await other.write(MAP.lease_owner, [65535])
    beat = Beater(other, MAP)
    await beat.start()
    try:
        report = await run(options(stub, motion=True))
    finally:
        await beat.stop()
        other.close()
    assert report.exit_code == 3
    assert "LeaseOwner (C+9) 65535" in report.checks[0].message


async def test_every_failure_path_reports_only_listed_observed_keys() -> None:
    for fault in ("suppress_ack", "watchdog_disabled", "swapped_word_order", "publish_limits"):
        async with StubPlc() as plc:
            setattr(plc.o, fault, fault != "publish_limits")
            report = await run(options(plc), checks=upto("CHK-10"))
        assert report.unknown_observed == set(), fault
        for c in report.checks:
            expected = [] if c.result == SKIPPED else [*OBSERVED[c.id], "retries"]
            assert list(c.observed) == expected, (fault, c.id)


async def _moving_commander(stub: StubPlc) -> tuple[PlcClient, Beater]:
    """A live commander as owner 1: beating, Enabled, in MoveVelocity (State 4) — the reviewer's #18 setup."""
    commander = PlcClient("127.0.0.1", stub.port, 1)
    await commander.connect()
    await commander.write(MAP.lease_owner, [1])
    beat = Beater(commander, MAP)
    await beat.start()
    await commander.write(MAP.target_position, [0, 0, 1000, 0, 0, 0])
    await commander.write(MAP.command, [int(Command.ENABLE | Command.MOVE_VELOCITY), 48])
    while stub.axis.state != 4:  # noqa: ASYNC110 — polls the stub's scan state; there is no event to await
        await asyncio.sleep(0.005)
    return commander, beat


async def test_run_interrupted_during_preflight_writes_nothing_to_a_live_commanders_axis(stub: StubPlc) -> None:
    # GA-U-70.py (review #18): Ctrl-C in the 1 s pre-flight watch → exit 4, no write at all, the commander untouched.
    commander, beat = await _moving_commander(stub)
    try:
        before = len(stub.writes)
        task = asyncio.create_task(run(options(stub, motion=True)))
        await asyncio.sleep(0.5)  # inside the 1 s Heartbeat watch
        task.cancel()
        report = await task
        ours = [w for w in stub.writes[before:] if w[0] != MAP.heartbeat]
        command = stub.regs[MAP.command : MAP.command + 2]
    finally:
        await beat.stop()
        commander.close()
    assert report.interrupted
    assert report.exit_code == 4
    assert all(c.result == SKIPPED for c in report.checks)
    assert report.checks[0].message == "interrupted by the operator during pre-flight"
    assert ours == []
    assert report.cleanup == []
    assert command == [int(Command.ENABLE | Command.MOVE_VELOCITY), 48]
    assert stub.axis.state == 4


async def test_run_interrupted_during_preflight_on_an_idle_axis_writes_nothing(stub: StubPlc) -> None:
    # GA-U-70.py (review #18), idle case: nothing was written before the interruption, so nothing is undone.
    task = asyncio.create_task(run(options(stub, motion=True)))
    await asyncio.sleep(0.5)
    task.cancel()
    report = await task
    assert report.exit_code == 4
    assert stub.writes == []
    assert report.cleanup == []


async def test_run_whose_preflight_read_fails_fails_chk01_skips_the_rest_and_writes_nothing(stub: StubPlc) -> None:
    # GA-U-71.py (review #4): the connect succeeds, the first C+8…C+9 read goes unanswered twice (the one retry
    # included). Pre-flight did not prove the axis free, so the run stops: CHK-01 FAIL Transport, nothing written.
    stub.drop_next = 2
    report = await run(options(stub), checks=upto("CHK-05"))
    chk01 = report.checks[0]
    assert chk01.result == FAIL
    assert chk01.error_class == "Transport"
    assert chk01.message.startswith(
        "Transport/CommunicationLost: pre-flight did not complete, nothing was written: read C+8…C+9"
    )
    assert chk01.observed["retries"] == 1
    assert all(c.result == SKIPPED and c.message == "needs CHK-01, which FAILED" for c in report.checks[1:])
    assert report.exit_code == 1
    assert stub.writes == []
    assert report.cleanup == []


@pytest.mark.timeout(60)
async def test_run_reports_a_dead_beat_during_homing_as_transport_not_a_watchdog_trip(stub: StubPlc) -> None:
    # GA-U-75.py (review #7): the beat's writes go unanswered once homing starts; the beat dies after its one retry.
    # The PLC then trips its watchdog, but the FAIL is the cause the checker saw: Transport, with the beat's exception.
    def heartbeat_write_while_homing(pdu: bytes) -> bool:
        return pdu[0] == 6 and int.from_bytes(pdu[1:3]) == MAP.heartbeat and stub.axis.state == 2

    stub.drop_if = heartbeat_write_while_homing
    wanted = {"CHK-01", "CHK-02", "CHK-06", "CHK-12"}
    report = await run(options(stub, motion=True), checks=tuple(c for c in CHECKS if c.id in wanted))
    chk12 = next(c for c in report.checks if c.id == "CHK-12")
    assert chk12.result == FAIL
    assert chk12.error_class == "Transport"
    assert chk12.message.startswith(
        "Transport/CommunicationLost: Heartbeat (C+8) write failed, the beat stopped: write C+8"
    ), chk12.message
    assert "WatchdogTripped" not in chk12.message


@pytest.mark.timeout(60)
async def test_run_reports_a_dead_beat_during_a_wait_even_when_the_plc_never_trips(stub: StubPlc) -> None:
    # GA-U-75.py (review #7), second case: with the PLC's watchdog off nothing trips, and homing would PASS with the
    # checker's beat dead. Every request of a running check consults the beat, so the wait FAILs Transport.
    def heartbeat_write_while_homing(pdu: bytes) -> bool:
        return pdu[0] == 6 and int.from_bytes(pdu[1:3]) == MAP.heartbeat and stub.axis.state == 2

    stub.o.watchdog_disabled = True
    stub.drop_if = heartbeat_write_while_homing
    wanted = {"CHK-01", "CHK-02", "CHK-06", "CHK-12"}
    report = await run(options(stub, motion=True), checks=tuple(c for c in CHECKS if c.id in wanted))
    chk12 = next(c for c in report.checks if c.id == "CHK-12")
    assert (chk12.result, chk12.error_class) == (FAIL, "Transport"), chk12.message
    assert "Heartbeat (C+8) write failed, the beat stopped" in chk12.message


@pytest.mark.timeout(120)
async def test_cleanup_waits_for_the_stop_ack_before_clearing_the_edge_on_a_slow_scan(stub: StubPlc) -> None:
    # GA-U-76.py (review #5): Ctrl-C mid-move on a PLC with a 100 ms scan. The Stop edge must stay set until the
    # PLC acknowledged it, else the scan sees the cleared word and the Stop is never executed.
    task = asyncio.create_task(run(options(stub, motion=True), checks=TO_THE_FIRST_MOVE))
    while stub.axis.state != 3:  # noqa: ASYNC110 — polls the stub's scan state; there is no event to await
        await asyncio.sleep(0.005)
    stub.o.scan_s = 0.1
    accepted_before = len(stub.accepted)
    task.cancel()
    report = await task
    assert report.exit_code == 4
    stop = int(Command.ENABLE | Command.STOP)
    assert stop in stub.accepted[accepted_before:], [hex(w) for w in stub.accepted[accepted_before:]]
    entries = report.cleanup
    assert entries[0].endswith("(Stop)")
    assert entries[1] == "C+0 = 0x0001 (clear edge bits)"
    assert not any("no CommandAck" in e for e in entries)


async def test_cleanup_journals_a_stop_that_was_not_acknowledged() -> None:
    # GA-U-76.py (review #5): no ack within 500 ms → the journal says so, and cleanup continues.
    async with StubPlc() as plc:
        task = asyncio.create_task(run(options(plc, motion=True), checks=TO_THE_FIRST_MOVE))
        while plc.axis.state != 3:  # noqa: ASYNC110 — polls the stub's scan state; there is no event to await
            await asyncio.sleep(0.005)
        plc.o.suppress_ack = True
        task.cancel()
        report = await task
    assert re.fullmatch(
        r"Stop: no CommandAck within 500 ms \(CommandSeq \d+ written, CommandAck \d+ read, State \d read\)",
        report.cleanup[1],
    ), report.cleanup
    assert "C+9 = 0 (release lease)" in report.cleanup
