"""Runner behaviour against the in-process stub PLC: GA-U-63.py (prerequisites) plus pre-flight, fault detection,
restore and cleanup. The stub is not the acceptance target; the C# simulator is (tests/test_integration.py)."""

from __future__ import annotations

import asyncio
import dataclasses
import logging
import re

import pytest

from generic_axis_check.beat import Beater
from generic_axis_check.checks import CHECKS, FAIL, OBSERVED, PASS, SKIPPED, Check, Outcome, beat_for
from generic_axis_check.client import PlcClient
from generic_axis_check.context import CheckContext, Options
from generic_axis_check.registers import Command, RegisterMap
from generic_axis_check.report import to_json, to_markdown
from generic_axis_check.runner import Report, normalize_observed, run

from .conftest import stub_options
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
    # Review #22: a refused run never reports PASS (protocol.md § Report schema).
    assert report.result == "REFUSED"
    assert to_markdown(report).endswith("\nRESULT: REFUSED\n")
    assert to_json(report)["summary"] == {"result": "REFUSED", "pass": 0, "fail": 0, "skipped": 16}
    assert to_json(report)["preflight"] == report.checks[0].message  # the refusal, also the line after the heading
    message = report.checks[0].message
    assert re.fullmatch(
        r"pre-flight: another commander is live: Heartbeat \(C\+8\) changed \d+( → \d+)+ within 1\.[01] s, "
        r"LeaseOwner \(C\+9\) 1; stop it first",
        message,
    )
    assert all(c.result == SKIPPED and c.message == message for c in report.checks)
    assert foreign == []
    assert stub.regs[MAP.lease_owner] == 1
    assert report.cleanup == []


async def test_run_refuses_a_held_lease_with_no_beat_and_no_trip(stub: StubPlc) -> None:
    # protocol.md "Pre-flight" (#35): a held lease, no beat and no trip within 1.6 s is a live commander or a PLC
    # without a working watchdog: refused, naming LeaseOwner and WatchdogFault, nothing written.
    stub.regs[MAP.lease_owner] = 1
    report = await run(options(stub), checks=upto("CHK-07"))
    assert report.exit_code == 3
    assert re.fullmatch(
        r"pre-flight: LeaseOwner \(C\+9 = 9\) = 1 is held and WatchdogFault \(C\+10 = 10\) = 0: no beat and no trip "
        r"within 1\.[67] s — a live commander, or a PLC without a working watchdog; release LeaseOwner by hand only if "
        r"no commander runs",
        report.checks[0].message,
    ), report.checks[0].message
    assert stub.writes == []


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
    # GA-U-100.py (review #18): Ctrl-C in the 1 s pre-flight watch → exit 4, no write at all, the commander untouched.
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
    # GA-U-100.py (review #18), idle case: nothing was written before the interruption, so nothing is undone.
    task = asyncio.create_task(run(options(stub, motion=True)))
    await asyncio.sleep(0.5)
    task.cancel()
    report = await task
    assert report.exit_code == 4
    assert stub.writes == []
    assert report.cleanup == []


async def test_run_whose_preflight_read_fails_fails_chk01_skips_the_rest_and_writes_nothing(stub: StubPlc) -> None:
    # GA-U-101.py (review #4): the connect succeeds, the first C+8…C+9 read goes unanswered twice (the one retry
    # included). Pre-flight did not prove the axis free, so the run stops: CHK-01 FAIL Transport, nothing written.
    stub.drop_next = 2
    report = await run(options(stub), checks=upto("CHK-05"))
    chk01 = report.checks[0]
    assert chk01.result == FAIL
    assert chk01.error_class == "Transport"
    assert chk01.message.startswith(
        "Transport/CommunicationLost: pre-flight did not complete, nothing was written: read C+8…C+10"
    )
    assert chk01.observed["retries"] == 1
    assert all(c.result == SKIPPED and c.message == "needs CHK-01, which FAILED" for c in report.checks[1:])
    assert report.exit_code == 1
    assert stub.writes == []
    assert report.cleanup == []


@pytest.mark.timeout(60)
async def test_run_reports_a_dead_beat_during_homing_as_transport_not_a_watchdog_trip(stub: StubPlc) -> None:
    # GA-U-105.py (review #7): the beat's writes go unanswered once homing starts; the beat dies after its one retry.
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
    # GA-U-105.py (review #7), second case: with the PLC's watchdog off nothing trips, and homing would PASS with the
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
    # GA-U-106.py (review #5): Ctrl-C mid-move on a PLC with a 100 ms scan. The Stop edge must stay set until the
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
    # GA-U-106.py (review #5): no ack within 500 ms → the journal says so, and cleanup continues.
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


async def test_an_unacknowledged_edge_is_in_last_read_before_the_edge_is_cleared() -> None:
    # GA-U-107.py (review #2 c): "lastRead is a fresh read … taken when the failure is detected and before any restore
    # write". The clear of an unacknowledged edge is such a write: C+0 in lastRead still shows the Reset edge.
    async def reset_without_ack(ctx: CheckContext) -> Outcome:
        await ctx.command(Command.RESET)
        return Outcome(PASS, "unreachable: the stub never acknowledges")

    check = dataclasses.replace(CHECKS[0], run=reset_without_ack)
    async with StubPlc() as plc:
        plc.o.suppress_ack = True
        report = await run(options(plc), checks=(check,))
        cleared = plc.regs[MAP.command]
    chk01 = report.checks[0]
    assert (chk01.result, chk01.error_class) == (FAIL, "Protocol")
    assert chk01.message.startswith("Protocol/NotAcknowledged: Reset not accepted. CommandSeq 1 written, CommandAck 0")
    assert chk01.last_read is not None
    assert chk01.last_read.command[:2] == [int(Command.RESET), 1]
    assert cleared == 0  # the edge was cleared after the evidence was taken


async def test_chk06_fails_an_axis_found_in_error_stop_and_commands_nothing(stub: StubPlc) -> None:
    # GA-U-108.py (review #9): "Precondition State 0 or 1" and "No silent recovery": the axis the checker found in
    # ErrorStop (FaultCode 2, limit switch) is reported as read, never Reset behind the operator's back.
    stub.axis.state, stub.axis.fault, stub.axis.enable_blocked = 7, 2, True
    wanted = {"CHK-01", "CHK-02", "CHK-06", "CHK-07", "CHK-11"}
    report = await run(options(stub), checks=tuple(c for c in CHECKS if c.id in wanted))
    chk06 = next(c for c in report.checks if c.id == "CHK-06")
    assert (chk06.result, chk06.error_class) == (FAIL, "Machine")
    assert chk06.message == (
        "Machine/LimitTripped: precondition: State 0 or 1 expected; reset the axis first. "
        "Read State (S+0 = 100) = 7, FaultCode (S+6 = 106) = 2."
    )
    # Lead ruling on #9: a precondition FAIL is a failure to restore: CHK-11 (needs only 02) is SKIPPED too.
    assert by_id(report)["CHK-07"] == (SKIPPED, "restore after CHK-06 failed")
    assert by_id(report)["CHK-11"] == (SKIPPED, "restore after CHK-06 failed")
    # Since #35 (b) the checker holds the lease and beats from the end of pre-flight; it never commands, Resets or
    # clears anything on this axis, and cleanup only releases its own lease.
    assert {address for address, _ in stub.writes} <= {MAP.heartbeat, MAP.lease_owner}
    assert report.cleanup == ["C+9 = 0 (release lease)"]
    assert stub.regs[MAP.lease_owner] == 0
    assert (stub.axis.state, stub.axis.fault) == (7, 2)


async def test_last_read_is_a_fresh_read_not_the_checks_last_values(stub: StubPlc) -> None:
    # GA-U-110.py (review #21 mutant 2): a register that changed after the check's last read shows its new value.
    async def read_then_fail(ctx: CheckContext) -> Outcome:
        await ctx.client.read(MAP.command, 12)
        await ctx.client.read(MAP.status, 15)
        stub.regs[MAP.watchdog_trips] = 42  # PLC-owned; changes after the check read it
        return Outcome(FAIL, "planted", {}, None)

    check = dataclasses.replace(CHECKS[0], run=read_then_fail)
    report = await run(options(stub), checks=(check,))
    last = report.checks[0].last_read
    assert last is not None
    assert last.command[11] == 42


async def test_run_refuses_a_commander_beating_with_lease_owner_0(stub: StubPlc) -> None:
    # GA-U-111.py (review #21 mutant 3, #16): "whatever LeaseOwner holds (0, …)" — a beat with LeaseOwner 0 refuses.
    commander = PlcClient("127.0.0.1", stub.port, 1)
    await commander.connect()
    beat = Beater(commander, MAP)
    await beat.start()
    try:
        before = len(stub.writes)
        report = await run(options(stub, motion=True))
        ours = [w for w in stub.writes[before:] if w[0] != MAP.heartbeat]
    finally:
        await beat.stop()
        commander.close()
    assert report.exit_code == 3
    assert report.result == "REFUSED"
    assert "LeaseOwner (C+9) 0;" in report.checks[0].message
    assert ours == []


async def test_beat_for_counts_from_the_step_not_from_the_beats_start(stub: StubPlc) -> None:
    # GA-U-112.py (review #13): the beat has run 1.5 s since restore; "beat for 2 s" still beats 2 s from the step.
    client = PlcClient("127.0.0.1", stub.port, 1)
    await client.connect()
    ctx = CheckContext(client, MAP, options(stub), Beater(client, MAP))
    try:
        await ctx.beater.start()
        await asyncio.sleep(1.5)
        started = asyncio.get_running_loop().time()
        await beat_for(ctx, 2.0)
        elapsed = asyncio.get_running_loop().time() - started
        assert ctx.beater.running
    finally:
        await ctx.beater.stop()
        client.close()
    assert elapsed >= 2.0


@pytest.mark.timeout(60)
async def test_ctrl_c_during_chk11c_leaves_no_lease_client_running_into_cleanup(
    stub: StubPlc, monkeypatch: pytest.MonkeyPatch
) -> None:
    # GA-U-113.py (review #12): cancel while (c)'s lease client watches. It is cancelled with the check, so it cannot
    # take the lease after cleanup released it: LeaseOwner stays 0.
    monkeypatch.setattr("generic_axis_check.checks.WATCH_BEFORE_STALL_S", 5.0)  # widen (c)'s watch window
    wanted = {"CHK-01", "CHK-02", "CHK-11"}
    task = asyncio.create_task(run(options(stub), checks=tuple(c for c in CHECKS if c.id in wanted)))
    while stub.regs[MAP.lease_owner] != 65534:  # noqa: ASYNC110 — (b) starts: the incumbent's id
        await asyncio.sleep(0.005)
    await asyncio.sleep(3.0 + 1.0)  # (b)'s 3 s refusal, then 1 s into (c)'s watch
    cancelled_at = len(stub.writes)
    task.cancel()
    report = await task
    leaked = [t for t in asyncio.all_tasks() if not t.done() and getattr(t.get_coro(), "__name__", "") == "acquire"]
    assert leaked == []
    assert report.exit_code == 4
    assert by_id(report)["CHK-11"] == (SKIPPED, "interrupted by the operator during CHK-11")
    await asyncio.sleep(2.0)  # longer than the lease expiry a leaked client would wait for
    assert (MAP.lease_owner, [65535]) not in stub.writes[cancelled_at:]
    assert stub.regs[MAP.lease_owner] != 65535


async def test_a_checker_defect_ends_in_a_report_exit_1_and_a_logged_traceback(
    stub: StubPlc, caplog: pytest.LogCaptureFixture
) -> None:
    # GA-U-114.py (review #14): an unexpected exception is not a PLC finding and not a bare traceback: the check
    # FAILs "checker defect" with no error class, the rest is SKIPPED, cleanup runs, exit 1, traceback at Error.
    async def broken(_ctx: CheckContext) -> Outcome:
        raise ZeroDivisionError("planted")

    checks = (FAKE[0], dataclasses.replace(FAKE[1], run=broken), *FAKE[2:])
    with caplog.at_level(logging.ERROR, logger="generic_axis_check.runner"):
        report = await run(options(stub), checks=checks)
    chk02 = report.checks[1]
    assert chk02.result == FAIL
    assert chk02.error_class is None
    assert chk02.message == (
        "checker error: ZeroDivisionError: planted (during CHK-02; the traceback is logged). Not a verdict on the "
        "PLC: repeat the run after the tool is fixed."
    )
    assert all(c.result == SKIPPED and c.message == "not run: checker error during CHK-02" for c in report.checks[2:])
    assert report.exit_code == 1
    assert to_json(report)["checks"][1]["errorClass"] is None
    record = next(r for r in caplog.records if r.getMessage() == "CHK-02: checker defect")
    assert record.exc_info is not None


@pytest.mark.timeout(60)
async def test_chk13_not_arrived_states_the_arrival_budget_it_used() -> None:
    # GA-U-115.py (review #10): the FAIL names the protocol's CHK-13 budget: 10 mm at 50 mm/s → 2 × 0.2 s + 5 s.
    wanted = {"CHK-01", "CHK-02", "CHK-03", "CHK-06", "CHK-12", "CHK-13"}
    async with StubPlc(stub_options(initial_position=20_000, stall_discrete=True)) as plc:
        report = await run(options(plc, motion=True), checks=tuple(c for c in CHECKS if c.id in wanted))
    chk13 = next(c for c in report.checks if c.id == "CHK-13")
    assert (chk13.result, chk13.error_class) == (FAIL, "Machine")
    assert chk13.message.startswith(
        "Machine/MotionFailed: not arrived in position within 5.4 s (2 × |target − start| ÷ velocity + 5 s)."
    ), chk13.message


def test_an_unknown_observed_key_is_logged_at_error_never_dropped_silently(caplog: pytest.LogCaptureFixture) -> None:
    # GA-U-116.py (review #23).
    unknown: set[str] = set()
    with caplog.at_level(logging.ERROR, logger="generic_axis_check.runner"):
        observed = normalize_observed("CHK-02", {"mapVersion": 1, "bogus": 7}, 0, unknown)
    assert observed == {"mapVersion": 1, "retries": 0}
    assert unknown == {"CHK-02.bogus"}
    assert [r.getMessage() for r in caplog.records] == [
        "CHK-02: observed key(s) outside § Observed values, not reported: bogus"
    ]


@pytest.mark.timeout(60)
async def test_a_lease_taken_by_another_owner_mid_run_fails_the_check_and_stops_all_writes(stub: StubPlc) -> None:
    # GA-U-118.py (lead ruling #33, protocol.md "Lease and beat between checks"): LeaseOwner = 7 appears while CHK-08
    # beats. CHK-08 FAILs Protocol/ProtocolMismatch, CHK-09…16 are SKIPPED with that reason, no restore, and after the
    # beat saw it nothing more is written to the axis; exit 1.
    started = asyncio.Event()

    def progress(line: str) -> None:
        if line.startswith("CHK-08 "):
            started.set()

    task = asyncio.create_task(run(options(stub, motion=True), progress))
    await started.wait()
    await asyncio.sleep(0.5)  # inside CHK-08's 2 s beat
    stub.regs[MAP.lease_owner] = 7
    changed_at = len(stub.writes)
    report = await task
    reason = "the lease did not hold. Read LeaseOwner (C+9 = 9) = 7, expected 65535."
    chk08 = next(c for c in report.checks if c.id == "CHK-08")
    assert (chk08.result, chk08.error_class) == (FAIL, "Protocol")
    assert chk08.message == f"Protocol/ProtocolMismatch: {reason}"
    assert all(c.result == SKIPPED and c.message == f"CHK-08: {reason}" for c in report.checks[8:])
    assert report.exit_code == 1
    after = stub.writes[changed_at:]
    assert all(address == MAP.heartbeat for address, _ in after), after
    assert len(after) <= 1  # at most the beat already in flight when LeaseOwner changed
    assert report.cleanup == ["C+8 (Heartbeat): stopped beating"]
    assert stub.regs[MAP.lease_owner] == 7


async def test_a_commander_silent_for_1_1_s_is_still_refused() -> None:
    # GA-U-119.py (#35): the PLC trips 1.4 s after the last beat; a commander that pauses 1.1 s as the checker starts
    # still holds a valid lease. The held-lease watch runs to 1.6 s, sees the beat resume, and refuses.
    async with StubPlc(stub_options(watchdog_s=1.4)) as plc:
        commander = PlcClient("127.0.0.1", plc.port, 1)
        await commander.connect()
        await commander.write(MAP.lease_owner, [1])
        beat = Beater(commander, MAP)
        await beat.start()
        await asyncio.sleep(0.3)  # armed
        try:
            await beat.stop()
            before = len(plc.writes)
            task = asyncio.create_task(run(options(plc, motion=True)))
            await asyncio.sleep(1.1)
            await beat.start()
            report = await task
            ours = [w for w in plc.writes[before:] if w[0] != MAP.heartbeat]
        finally:
            await beat.stop()
            commander.close()
        fault = plc.regs[MAP.watchdog_fault]
    assert report.exit_code == 3
    assert report.result == "REFUSED"
    assert "another commander is live" in report.checks[0].message
    # Review #27: the message names the window actually watched (the beat resumed after 1.1 s), not "1 s".
    watched = re.search(r"within (\d\.\d) s,", report.checks[0].message)
    assert watched is not None
    assert float(watched.group(1)) >= 1.1, report.checks[0].message
    assert ours == []
    assert fault == 0


@pytest.mark.timeout(60)
async def test_a_dead_commander_whose_watchdog_trips_lets_the_run_proceed_and_its_trip_stays() -> None:
    # GA-U-120.py (#35): the lease holder (owner 1, Enabled) dies just before the run; its watchdog trips during the
    # watch. The run proceeds and says so after the heading; CHK-06 FAILs Machine/WatchdogTripped; nothing clears the
    # trip, the lease or the Enable it left: cleanup is empty.
    async with StubPlc() as plc:
        commander = PlcClient("127.0.0.1", plc.port, 1)
        await commander.connect()
        await commander.write(MAP.lease_owner, [1])
        beat = Beater(commander, MAP)
        await beat.start()
        await commander.write(MAP.command, [int(Command.ENABLE), 5])
        await asyncio.sleep(0.3)
        await beat.stop()  # dies
        commander.close()
        report = await run(options(plc, motion=True))
        end = (plc.regs[MAP.watchdog_fault], plc.regs[MAP.lease_owner], plc.axis.state, plc.regs[MAP.command])
    note = (
        "LeaseOwner (C+9 = 9) = 1 held with no beat and WatchdogFault (C+10 = 10) = 1: the previous commander is dead; "
        "its trip is left for its operator."
    )
    assert report.preflight == note
    assert to_json(report)["preflight"] == note
    assert to_markdown(report).splitlines()[2] == f"Pre-flight: {note}"
    results = by_id(report)
    assert [results[f"CHK-{n:02d}"][0] for n in range(1, 6)] == [PASS] * 5
    chk06 = report.checks[5]
    assert (chk06.result, chk06.error_class) == (FAIL, "Machine")
    assert chk06.message.startswith("Machine/WatchdogTripped: ")
    assert all(c.result == SKIPPED for c in report.checks[6:])
    assert report.cleanup == []
    assert end == (1, 1, 7, int(Command.ENABLE))
    assert report.exit_code == 1


@pytest.mark.timeout(60)
async def test_a_second_tool_started_during_the_firsts_chk03_is_refused(stub: StubPlc) -> None:
    # GA-U-122.py (#35 b): the first tool holds the lease and beats from the end of pre-flight, so a second tool with
    # the same id started during CHK-03 is refused, naming LeaseOwner 65535; the first run passes.
    in_chk03 = asyncio.Event()

    def progress(line: str) -> None:
        if line.startswith("CHK-03 "):
            in_chk03.set()

    first = asyncio.create_task(run(options(stub), progress, checks=upto("CHK-05")))
    await in_chk03.wait()
    second = await run(options(stub), checks=upto("CHK-05"))
    report = await first
    assert second.exit_code == 3
    assert second.result == "REFUSED"
    assert "LeaseOwner (C+9) 65535" in second.checks[0].message
    assert second.cleanup == []
    assert report.exit_code == 0
    assert stub.regs[MAP.lease_owner] == 0


async def test_a_commander_still_beating_after_a_trip_is_refused_not_declared_dead(stub: StubPlc) -> None:
    # GA-U-123.py (review #25): WatchdogFault = 1 at the first read does not make the holder dead. It keeps beating,
    # so the watch sees Heartbeat change: REFUSED, and nothing at all is written to its axis.
    commander = PlcClient("127.0.0.1", stub.port, 1)
    await commander.connect()
    await commander.write(MAP.lease_owner, [1])
    beat = Beater(commander, MAP)
    await beat.start()
    stub.regs[MAP.watchdog_fault] = 1  # an old trip, latched; the commander is alive and beating
    try:
        before = len(stub.writes)
        report = await run(options(stub, motion=True))
        ours = [w for w in stub.writes[before:] if w[0] != MAP.heartbeat]
    finally:
        await beat.stop()
        commander.close()
    assert report.exit_code == 3
    assert report.result == "REFUSED"
    assert "another commander is live" in report.checks[0].message
    assert ours == []
    assert report.cleanup == []
