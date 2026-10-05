"""One-verb mode (``--command``, protocol.md § Conformance checks › Command line, "One-verb mode"; ADR-38) against
the stub PLC: GA-U-145.py … GA-U-151.py (C# GA-I-71…80 and GA-U-145 against the simulator)."""

from __future__ import annotations

import asyncio
import re
import sys

import pytest

from generic_axis_check.__main__ import UsageError, main, parse
from generic_axis_check.beat import Beater
from generic_axis_check.checks import CHECKS, FAIL, PASS, SKIPPED
from generic_axis_check.client import PlcClient
from generic_axis_check.context import Options
from generic_axis_check.errors import SPEED_REFUSAL_PREFIX, speed_zero_body
from generic_axis_check.registers import Command, RegisterMap
from generic_axis_check.runner import CheckResult, prerequisite_problem, run
from generic_axis_check.verb import MOTION_VERBS, RESULT_EXIT, VERBS, Verb, VerbResult, raw, run_verb

from .stub_plc import StubOptions, StubPlc

MAP = RegisterMap()
ENABLE, HOME, MOVE_ABS, MOVE_VEL, STOP, RESET = 1, 2, 4, 8, 16, 32
DISABLED, STANDSTILL, CONTINUOUS, ERROR_STOP = 0, 1, 4, 7


async def verb(plc: StubPlc, v: Verb, lines: list[str] | None = None) -> VerbResult:
    out = lines if lines is not None else []
    result = await run_verb(Options(host="127.0.0.1", port=plc.port, allow_motion=True), v, out.append)
    await asyncio.sleep(3 * plc.o.scan_s)  # cleanup's last writes are scanned by the PLC after the tool returns
    return result


def row_sign(rows: list[str], text: str) -> str:
    return next(row[0] for row in rows if text in row)


def released(plc: StubPlc) -> bool:
    return plc.regs[MAP.lease_owner] == 0


# --- GA-U-146.py: each verb's happy path, through the handshake, printing every status read ------------------------


async def test_enable_reaches_standstill_and_ends_disabled(stub: StubPlc) -> None:
    lines: list[str] = []
    result = await verb(stub, Verb("enable"), lines)
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert re.fullmatch(r"enable: done — Standstill after \d+ ms\.", result.message), result.message
    assert stub.accepted == [ENABLE, 0]  # Enable 1, then cleanup's Enable 0: no more energised than found
    assert result.cleanup == ["C+0 = 0x0000, C+1 = 2 (Enable 0)", "C+9 = 0 (release lease)"]
    assert released(stub)
    assert stub.axis.state == DISABLED
    # Step 5: every status read printed with the six registers.
    # The C# tool's shape: a header, one line per command write, one per 20 ms status read, ms from the first write.
    assert lines[0] == f"--command enable on 127.0.0.1:{stub.port} unit 1 (C = holding 0, S = input 0), owner 65535"
    writes = [line for line in lines if "  write Command " in line]
    assert writes == ["+     0 ms  write Command Enable (0x0001)"], writes
    rows = [line for line in lines if " ms  State " in line]
    assert rows, lines
    for row in rows:
        assert re.fullmatch(
            r"[+-] {0,5}\d+ ms  State \d \w+  Flags \S+  ActualPosition -?\d+\.\d{3}  ActualVelocity -?\d+\.\d{3}  "
            r"FaultCode \d+ \S.*  CommandAck \d+",
            row,
        ), row
    assert any("State 1 Standstill  Flags Homed|DriveReady  " in row and "FaultCode 0 None" in row for row in rows)
    # ms count from the completion of the verb's first write: the read that shows Standstill comes after it (it can be
    # +0 ms when the scan lands between the write's apply and its answer, so only the sign is pinned here).
    assert row_sign(rows, "State 1 Standstill") == "+"


async def test_disable_reaches_disabled(stub: StubPlc) -> None:
    stub.regs[MAP.command] = ENABLE
    stub.axis.state = STANDSTILL  # an axis found energised, at rest
    result = await verb(stub, Verb("disable"))
    assert (result.result, result.exit_code) == ("PASS", 0)
    assert re.fullmatch(r"disable: done — Disabled after \d+ ms\.", result.message), result.message
    assert stub.axis.state == DISABLED
    assert result.cleanup == ["C+9 = 0 (release lease)"]


async def test_stop_keeps_the_enable_it_found_and_cleanup_leaves_it(stub: StubPlc) -> None:
    # Step 6: "Enable 0 only if this run set Enable 1": an axis found enabled stays enabled after `stop`.
    stub.regs[MAP.command] = ENABLE
    stub.axis.state = STANDSTILL
    result = await verb(stub, Verb("stop"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert re.fullmatch(r"stop: done — Standstill after \d+ ms\.", result.message), result.message
    assert stub.accepted == [ENABLE | STOP]
    assert stub.regs[MAP.command] == ENABLE  # the edge cleared after the ack, Enable kept
    assert result.cleanup == ["C+9 = 0 (release lease)"]


async def test_reset_leaves_errorstop_after_clearing_watchdog_fault_and_enable(stub: StubPlc) -> None:
    stub.regs[MAP.command] = ENABLE
    stub._error_stop(2)  # a limit-switch fault, as the PLC latches it
    stub.regs[MAP.watchdog_fault] = 1
    result = await verb(stub, Verb("reset"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert stub.regs[MAP.watchdog_fault] == 0
    assert stub.accepted == [0, RESET]  # "Enable 0 before the Reset edge"
    assert (stub.axis.state, stub.axis.fault) == (DISABLED, 0)


async def test_home_enables_first_and_ends_homed_and_disabled() -> None:
    async with StubPlc(StubOptions(homing_velocity=5_000_000)) as plc:
        plc.axis.homed = False
        result = await verb(plc, Verb("home"))
    assert (result.result, result.exit_code) == ("PASS", 0)
    assert re.fullmatch(r"home: done — Standstill \+ Homed after \d+ ms\.", result.message), result.message
    assert plc.accepted[:2] == [ENABLE, ENABLE | HOME]  # "from Disabled set Enable first"
    assert plc.axis.homed
    assert plc.axis.state == DISABLED  # cleanup's Enable 0
    assert released(plc)


async def test_move_writes_parameters_then_the_command_and_arrives(stub: StubPlc) -> None:
    lines: list[str] = []
    result = await verb(stub, Verb("move", 600, speed_percent=20), lines)
    # ms count from the verb's first write, not from the start of the run: the 100 mm move takes about 1 s.
    rows = [line for line in lines if " ms  State " in line]
    assert rows[-1].startswith("+"), rows[-1]
    assert "  ActualPosition 600.000  ActualVelocity 0.000  " in rows[-1], rows[-1]  # axis units, not raw
    assert 800 < int(rows[-1][1:7]) < 1800, rows[-1]  # 100 mm at 100 mm/s, 1 m/s² ramps: about 1.05 s
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert re.fullmatch(
        r"move: done — Standstill \+ InPosition after \d+ ms, ActualPosition 600\.000\.", result.message
    ), result.message
    # Handshake: parameters C+2…C+7 in one FC16 (target 600 000, 20 % of 500 000), then C+0…C+1 in a second FC16.
    params = [(a, v) for a, v in stub.writes if a == MAP.target_position]
    assert params == [(2, [0x27C0, 0x0009, 0x86A0, 0x0001, 0, 0])]
    assert stub.writes.index(params[0]) < stub.writes.index((0, [ENABLE | MOVE_ABS, 2]))
    assert round(stub.axis.p) == 600_000
    assert stub.axis.state == DISABLED
    assert released(stub)


async def test_jog_for_runs_continuous_motion_then_stops() -> None:
    async with StubPlc(StubOptions(default_acceleration=5_000_000)) as plc:
        result = await verb(plc, Verb("jog", -50, run_for_s=0.4))
        start, end = 500_000, plc.axis.p
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert re.fullmatch(
        r"jog: done — ContinuousMotion after \d+ ms; Stop → Standstill after \d+ ms\.", result.message
    ), result.message
    assert not any(line.endswith("(Stop)") for line in result.cleanup), result.cleanup  # the verb stopped, not cleanup
    assert ENABLE | MOVE_VEL in plc.accepted
    assert ENABLE | STOP in plc.accepted
    assert plc.accepted.index(ENABLE | STOP) > plc.accepted.index(ENABLE | MOVE_VEL)
    assert end < start  # moved in the negative direction
    assert plc.axis.state == DISABLED
    assert released(plc)


# --- GA-U-147.py: the PLC fails the verb → exit 1 in rule 1's shape ------------------------------------------------


async def test_a_verb_never_acknowledged_is_protocol_not_acknowledged() -> None:
    async with StubPlc(StubOptions(suppress_ack=True)) as plc:
        result = await verb(plc, Verb("enable"))
    assert (result.result, result.exit_code) == ("FAIL", 1)
    assert re.fullmatch(
        r"enable: Protocol/NotAcknowledged: Enable 1 not accepted\. CommandSeq 1 written, CommandAck 0 read after "
        r"\d+ ms, State 0 read\.",
        result.message,
    ), result.message
    assert released(plc)


async def test_a_machine_that_never_arrives_is_machine_motion_failed() -> None:
    async with StubPlc(StubOptions(stall_discrete=True)) as plc:
        result = await verb(plc, Verb("move", 499.99, speed_percent=100))  # a 0.01 budget: 5 s
    assert (result.result, result.exit_code) == ("FAIL", 1)
    assert result.message.startswith("move: Machine/MotionFailed: not arrived in position within 5.0 s"), result.message
    assert "ActualPosition (S+2 = input 2) = 500000, expected 499990" in result.message


async def test_a_wrong_map_version_is_protocol_and_writes_nothing() -> None:
    async with StubPlc(StubOptions(map_version=2)) as plc:
        result = await verb(plc, Verb("enable"))
        writes = list(plc.writes)
    assert (result.result, result.exit_code) == ("FAIL", 1)
    assert result.message == (
        "enable: Protocol/ProtocolMismatch: MapVersion not 1. Read MapVersion (S+14 = input 14) = 2, expected 1."
    )
    assert writes == []
    assert result.cleanup == []


async def test_partial_limits_are_protocol_and_write_nothing(stub: StubPlc) -> None:
    stub.o.max_velocity = 0  # TravelMin < TravelMax published, MaxVelocity 0: a partial publication
    result = await verb(stub, Verb("stop"))
    assert (result.result, result.exit_code) == ("FAIL", 1)
    assert result.message.startswith("stop: Protocol/ProtocolMismatch: limits not sane"), result.message
    assert stub.writes == []


# --- GA-U-148.py: guards refuse before any write (Commander, exit 2), naming the register --------------------------


@pytest.mark.parametrize(
    ("v", "setup", "expected"),
    [
        (
            Verb("move", 20_000),
            None,
            "move: Commander/OutOfRange: refused before writing anything: target 20000.000 (raw 20000000) is outside TravelMin..TravelMax. "
            "Read TravelMin (S+8 = input 8) = 0, TravelMax (S+10 = input 10) = 10000000.",
        ),
        (
            Verb("move", -0.001),
            None,
            "move: Commander/OutOfRange: refused before writing anything: target -0.001 (raw -1) is outside TravelMin..TravelMax. "
            "Read TravelMin (S+8 = input 8) = 0, TravelMax (S+10 = input 10) = 10000000.",
        ),
        (
            Verb("move", 100),
            "unhomed",
            "move: Commander/NotHomed: refused before writing anything: the axis is not homed. Read Flags (S+1 = input 1) = 32.",
        ),
        (
            Verb("jog", -500.001, run_for_s=0.2),  # bounded: a guard that lets it through ends, red
            None,
            "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got -500.001 "
            "(raw -500001). Read MaxVelocity (S+12 = input 12) = 500000.",
        ),
        (
            Verb("jog", 0, run_for_s=0.2),
            None,
            "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got 0.000 "
            "(raw 0). Read MaxVelocity (S+12 = input 12) = 500000.",
        ),
        (
            Verb("jog", 0.0004, run_for_s=0.2),  # review #37: raw 0 is no motion, whatever was typed
            None,
            "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got 0.0004 "
            "(raw 0). Read MaxVelocity (S+12 = input 12) = 500000.",
        ),
        (
            Verb("jog", -0.0004, run_for_s=0.2),
            None,
            "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got -0.0004 "
            "(raw 0). Read MaxVelocity (S+12 = input 12) = 500000.",
        ),
        (
            Verb("jog", 500.001, run_for_s=0.2),  # MaxVelocity + one raw quantum
            None,
            "jog: Commander/UnreachableSpeed: refused before writing anything: jog needs 0 < |v| ≤ MaxVelocity, got 500.001 "
            "(raw 500001). Read MaxVelocity (S+12 = input 12) = 500000.",
        ),
        (
            Verb(
                "move", 100, speed_percent=150, speed_text="150"
            ),  # review #39: a value the tool would refuse is a guard
            None,
            "move: Commander/UnreachableSpeed: refused before writing anything: speed 150 % outside 0 < pct ≤ 100.",
        ),
        (
            Verb("move", 100, speed_percent=0, speed_text="0"),
            None,
            "move: Commander/UnreachableSpeed: refused before writing anything: speed 0 % outside 0 < pct ≤ 100.",
        ),
        (
            Verb("move", 100, speed_percent=0.00009, speed_text="0.00009"),  # 0.45 raw rounds to Velocity 0
            None,
            "move: Commander/UnreachableSpeed: refused before writing anything: 0.00009 % of MaxVelocity rounds to "
            "raw Velocity 0 (round-half-away-from-zero(0.00009 × 500000 ÷ 100) = 0); nothing to move with. "
            "Read MaxVelocity (S+12 = input 12) = 500000.",  # the percentage as given, never rounded for display
        ),
        (
            Verb("jog", 1, run_for_s=0.2),
            "unpublished",
            "jog: Commander/UnreachableSpeed: refused before writing anything: the PLC publishes no limits. Read TravelMin (S+8 = input 8) "
            "= 0, TravelMax (S+10 = input 10) = 0, MaxVelocity (S+12 = input 12) = 0.",
        ),
        (
            Verb("move", 1),
            "unpublished",
            "move: Commander/OutOfRange: refused before writing anything: the PLC publishes no limits. Read TravelMin (S+8 = input 8) "
            "= 0, TravelMax (S+10 = input 10) = 0, MaxVelocity (S+12 = input 12) = 0.",
        ),
    ],
)
@pytest.mark.timeout(30)  # a guard that lets a verb through must fail here, not wait out the verb's own budget
async def test_a_guard_refuses_before_any_write(stub: StubPlc, v: Verb, setup: str | None, expected: str) -> None:
    if setup == "unhomed":
        stub.axis.homed = False
    if setup == "unpublished":
        stub.o.publish_limits = False
    await asyncio.sleep(0.03)  # one scan publishes the change
    result = await verb(stub, v)
    assert (result.result, result.exit_code, result.message) == ("GUARD", 2, expected)
    assert stub.writes == []
    assert result.cleanup == []


async def test_the_guards_allow_the_limits_themselves(stub: StubPlc) -> None:
    # Inclusive bounds: a move to TravelMax and a jog at exactly MaxVelocity pass the guard.
    stub.axis.p = 9_999_000.0
    result = await verb(stub, Verb("move", 10_000, speed_percent=100))
    assert result.exit_code == 0, result.message
    result = await verb(stub, Verb("jog", -500, run_for_s=0.1))
    assert result.exit_code == 0, result.message


async def test_a_move_target_is_judged_on_its_raw_value(stub: StubPlc) -> None:
    # Review #37/#38: -0.0004 is written as raw 0 = TravelMin, so the guard lets it through and the move arrives.
    stub.axis.p = 1000.0
    result = await verb(stub, Verb("move", -0.0004, speed_percent=100))
    assert result.exit_code == 0, result.message
    params = [values for address, values in stub.writes if address == MAP.target_position]
    assert params[0][:2] == [0, 0], params  # TargetPosition raw 0


# --- GA-U-149.py: Ctrl-C ---------------------------------------------------------------------------------------------


async def test_ctrl_c_mid_jog_stops_and_releases_and_counts_as_completed() -> None:
    async with StubPlc(StubOptions(default_acceleration=5_000_000)) as plc:
        lines: list[str] = []
        task = asyncio.create_task(verb(plc, Verb("jog", 20), lines))
        while plc.axis.state != CONTINUOUS:  # noqa: ASYNC110 — waits for the stub's scan
            await asyncio.sleep(0.01)
        await asyncio.sleep(0.6)  # past the 500 ms ContinuousMotion window: the verb has completed
        task.cancel()
        result = await task
        await asyncio.sleep(0.03)
        state = plc.axis.state
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert plc.accepted.index(ENABLE | STOP) > plc.accepted.index(ENABLE | MOVE_VEL)
    assert state == DISABLED
    assert released(plc)


async def test_ctrl_c_before_the_verb_completes_is_exit_4_and_cleanup_stops() -> None:
    async with StubPlc(StubOptions(homing_velocity=5_000)) as plc:  # a slow home: still Homing when cancelled
        plc.axis.homed = False
        task = asyncio.create_task(verb(plc, Verb("home")))
        while plc.axis.state != 2:  # noqa: ASYNC110 — waits for Homing
            await asyncio.sleep(0.01)
        task.cancel()
        result = await task
        await asyncio.sleep(0.03)
        state = plc.axis.state
    assert (result.result, result.exit_code) == ("INTERRUPTED", 4)
    assert result.message == "interrupted by the operator before home completed"
    assert result.cleanup[0].endswith("(Stop)"), result.cleanup
    assert result.cleanup[-1] == "C+9 = 0 (release lease)"
    assert ENABLE | STOP in plc.accepted
    assert state == DISABLED
    assert released(plc)


# --- GA-U-150.py: pre-flight, dead holder, exit 3 --------------------------------------------------------------------


async def test_a_live_commander_refuses_with_exit_3_and_nothing_written(stub: StubPlc) -> None:
    commander = PlcClient("127.0.0.1", stub.port, 1)
    await commander.connect()
    await commander.write(MAP.lease_owner, [1])
    beat = Beater(commander, MAP)
    await beat.start()
    try:
        before = len(stub.writes)
        result = await verb(stub, Verb("stop"))
        ours = [w for w in stub.writes[before:] if w[0] != MAP.heartbeat]
    finally:
        await beat.stop()
        commander.close()
    assert (result.result, result.exit_code) == ("REFUSED", 3)
    assert result.message.startswith("Pre-flight: another commander is live: Heartbeat (C+8 = holding 8) = ")
    assert "LeaseOwner (C+9 = holding 9) = 1" in result.message
    assert ours == []
    assert result.cleanup == []


async def test_after_a_dead_holders_trip_the_tool_attaches_and_reset_recovers() -> None:
    # Step 1: "take the lease and write WatchdogFault = 0, leaving ErrorStop and FaultCode = 4 for reset".
    async with StubPlc() as plc:
        commander = PlcClient("127.0.0.1", plc.port, 1)
        await commander.connect()
        await commander.write(MAP.lease_owner, [1])
        beat = Beater(commander, MAP)
        await beat.start()
        await asyncio.sleep(0.3)
        await beat.stop()  # dies; its watchdog trips during the pre-flight watch
        commander.close()
        lines: list[str] = []
        result = await verb(plc, Verb("reset"), lines)
        end = (plc.regs[MAP.watchdog_fault], plc.axis.state, plc.axis.fault)
    # Review #40: the one-verb note says what this mode does with the trip (the checklist leaves it for its operator).
    assert re.fullmatch(
        r"Pre-flight: LeaseOwner \(C\+9 = holding 9\) = 1 held with no beat for 1\.\d s and WatchdogFault "
        r"\(C\+10 = holding 10\) = 1: the previous commander is dead\.",
        lines[1],
    ), lines[1]
    assert (
        "C+10 = holding 10 = 0 written at attach, as the driver does (ErrorStop and FaultCode 4 stay for reset)"
        in lines
    )
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert end == (0, DISABLED, 0)
    assert released(plc)


async def test_after_a_dead_holders_trip_watchdog_fault_is_cleared_at_attach_before_the_verb() -> None:
    # Step 1, "as the driver does at attach": WatchdogFault = 0 right after the lease, before the verb's first command
    # write; ErrorStop and FaultCode 4 stay (a `stop` does not reset them).
    async with StubPlc() as plc:
        commander = PlcClient("127.0.0.1", plc.port, 1)
        await commander.connect()
        await commander.write(MAP.lease_owner, [1])
        beat = Beater(commander, MAP)
        await beat.start()
        await asyncio.sleep(0.3)
        await beat.stop()
        commander.close()
        before = len(plc.writes)
        result = await verb(plc, Verb("stop"))
        writes = plc.writes[before:]
        end = (plc.regs[MAP.watchdog_fault], plc.axis.state, plc.axis.fault)
    cleared = writes.index((MAP.watchdog_fault, [0]))
    first_command = next(i for i, (address, _values) in enumerate(writes) if address == MAP.command)
    assert writes.index((MAP.lease_owner, [65535])) < cleared < first_command, writes
    assert result.exit_code == 1  # Stop from ErrorStop: State stays 7, the PLC's fault named
    assert result.message.startswith("stop: Machine/WatchdogTripped: Stop: ErrorStop"), result.message
    assert end == (0, ERROR_STOP, 4)


async def test_a_refused_guard_after_a_dead_holders_trip_writes_nothing_at_all() -> None:
    # protocol.md step 3: "A refused guard writes nothing at all: no lease, no beat", and only then the attach's
    # WatchdogFault = 0. Every stub write is recorded (not the end state: a write undone later must still show).
    async with StubPlc() as plc:
        commander = PlcClient("127.0.0.1", plc.port, 1)
        await commander.connect()
        await commander.write(MAP.lease_owner, [1])
        beat = Beater(commander, MAP)
        await beat.start()
        await asyncio.sleep(0.3)
        await beat.stop()  # dies; its watchdog trips during the pre-flight watch
        commander.close()
        before = len(plc.writes)
        lines: list[str] = []
        result = await verb(plc, Verb("move", 20_000), lines)
        ours = plc.writes[before:]
        end = (plc.regs[MAP.watchdog_fault], plc.regs[MAP.lease_owner])
    assert (result.result, result.exit_code) == ("GUARD", 2), result.message
    assert ours == []
    # Review #40 (C# #64): the note says what was seen; nothing claims a clear that was never written.
    assert lines[1].endswith("= 1: the previous commander is dead."), lines[1]
    assert not any("cleared" in line or "written at attach" in line for line in lines), lines
    assert end == (1, 1)  # the dead holder's trip and lease, untouched


# --- GA-U-145.py: the command line -----------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("argv", "expected"),
    [
        (["plc", "--command", "enable"], Verb("enable")),
        (
            ["plc", "--command", "move", "1500", "--speed", "20", "--allow-motion"],
            Verb("move", 1500, 20, speed_text="20"),
        ),
        (["plc", "--command", "move", "1500", "--allow-motion"], Verb("move", 1500, 10, speed_text="10")),
        (["plc", "--command", "jog", "-50", "--for", "2", "--allow-motion"], Verb("jog", -50, 10, 2)),
        (["plc", "--command", "jog", "-0.5", "--allow-motion"], Verb("jog", -0.5)),
    ],
)
def test_parse_reads_the_verb(argv: list[str], expected: Verb) -> None:
    assert parse(argv).verb == expected


@pytest.mark.parametrize(
    "argv",
    [
        ["plc", "--command", "home"],  # motion without --allow-motion
        ["plc", "--command", "move", "10"],
        ["plc", "--command", "jog", "5"],
        ["plc", "--command", "fly"],
        ["plc", "--command", "enable", "1"],
        ["plc", "--command", "move", "--allow-motion"],
        ["plc", "--command", "move", "x", "--allow-motion"],
        ["plc", "--command", "enable", "--speed", "10"],
        ["plc", "--command", "move", "10", "--for", "1", "--allow-motion"],
        ["plc", "--command", "jog", "5", "--for", "0", "--allow-motion"],
        ["plc", "--command", "enable", "--dump"],
        ["plc", "--command", "enable", "--report", "r.md"],
        ["plc", "--for", "1"],
        ["plc", "--command", "Home", "--allow-motion"],  # verbs are lower case
        ["plc", "--command", "enable", "--command", "disable"],
    ],
)
def test_parse_refuses_a_malformed_verb(argv: list[str]) -> None:
    with pytest.raises(UsageError):
        parse(argv)


def test_a_non_numeric_speed_stays_a_usage_error() -> None:
    # Review #39: a speed the tool would refuse is a guard; syntax stays usage (exit 2, no RESULT line).
    with pytest.raises(UsageError, match="--speed fast: not a number"):
        parse(["plc", "--command", "move", "10", "--speed", "fast", "--allow-motion"])


def test_main_exit_codes_for_usage_and_guard(capsys: pytest.CaptureFixture[str]) -> None:
    assert main(["127.0.0.1:1", "--command", "home"]) == 2
    assert "needs --allow-motion" in capsys.readouterr().err


async def test_cli_guard_refusal_prints_and_exits_2(stub: StubPlc) -> None:
    process = await asyncio.create_subprocess_exec(
        sys.executable,
        "-m",
        "generic_axis_check",
        f"127.0.0.1:{stub.port}",
        "--command",
        "move",
        "20000",
        "--allow-motion",
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    stdout, _ = await process.communicate()
    out = stdout.decode().splitlines()
    assert process.returncode == 2
    assert out[-1] == "RESULT: GUARD"
    assert out[-2].startswith("move: Commander/OutOfRange: refused before writing anything: target 20000.000")
    assert stub.writes == []


# --- GA-U-151.py: a jog whose ContinuousMotion never comes is Machine, and Stop still runs ---------------------------


async def test_a_jog_without_continuous_motion_fails_machine() -> None:
    async with StubPlc(StubOptions(suppress_ack=False)) as plc:
        plc.o.travel_min = 500_000  # at TravelMin already: a negative jog is a controlled stop to Standstill at once
        await asyncio.sleep(0.03)
        result = await verb(plc, Verb("jog", -50))
        state = plc.axis.state
    assert (result.result, result.exit_code) == ("FAIL", 1), result.message
    assert result.message.startswith("jog: Machine/MotionFailed: no ContinuousMotion 500 ms after the MoveVelocity ack")
    assert state == DISABLED
    assert released(plc)


def test_command_bits_match_the_protocol() -> None:
    assert tuple(
        int(c)
        for c in (
            Command.ENABLE,
            Command.HOME,
            Command.MOVE_ABSOLUTE,
            Command.MOVE_VELOCITY,
            Command.STOP,
            Command.RESET,
        )
    ) == (ENABLE, HOME, MOVE_ABS, MOVE_VEL, STOP, RESET)


# --- GA-U-152.py (#61): Enable is a fresh 0→1 edge; "found energised" is read from State ----------------------------


def pendant_reset(plc: StubPlc) -> None:
    """After a trip, a Reset with Enable bit 0 kept (a pendant reset): Disabled, C+0 bit 0 = 1, energising blocked
    until a fresh 0→1 (protocol.md § Command semantics, Enable)."""
    plc.regs[MAP.command] = ENABLE
    plc.axis.state = DISABLED
    plc.axis.enable_blocked = True


def command_writes(plc: StubPlc, since: int = 0) -> list[list[int]]:
    """Every write to C+0…C+1 in the stub's journal (not the end state)."""
    return [values for address, values in plc.writes[since:] if address == MAP.command]


async def test_enable_over_a_set_enable_bit_writes_the_0_edge_first_and_ends_disabled(stub: StubPlc) -> None:
    pendant_reset(stub)
    result = await verb(stub, Verb("enable"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    # Enable 0 as its own acknowledged command write (seq 1), then Enable 1 (seq 2), then cleanup's Enable 0 (seq 3).
    assert command_writes(stub) == [[0, 1], [ENABLE, 2], [0, 3]]
    assert stub.accepted == [0, ENABLE, 0]
    assert stub.axis.state == DISABLED


async def test_chk06_over_a_set_enable_bit_passes_with_the_0_edge_first(stub: StubPlc) -> None:
    pendant_reset(stub)
    wanted = {"CHK-01", "CHK-02", "CHK-06"}
    report = await run(Options(host="127.0.0.1", port=stub.port), checks=tuple(c for c in CHECKS if c.id in wanted))
    chk06 = next(c for c in report.checks if c.id == "CHK-06")
    assert chk06.result == "PASS", chk06.message
    assert command_writes(stub) == [[0, 1], [ENABLE, 2], [0, 3]]  # the later numbers shift by one


async def test_found_standstill_home_ends_standstill_with_no_enable_0(stub: StubPlc) -> None:
    stub.regs[MAP.command] = ENABLE
    stub.axis.state = STANDSTILL
    stub.axis.homed = False
    result = await verb(stub, Verb("home"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    words = [values[0] for values in command_writes(stub)]
    assert 0 not in words, words  # no Enable 0 anywhere, cleanup included
    assert not any("(Enable 0)" in line for line in result.cleanup), result.cleanup
    assert stub.axis.state == STANDSTILL


async def test_found_standstill_enable_ends_standstill_with_no_enable_0(stub: StubPlc) -> None:
    stub.regs[MAP.command] = ENABLE
    stub.axis.state = STANDSTILL
    result = await verb(stub, Verb("enable"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    assert command_writes(stub) == [[ENABLE, 1]]  # Enable 1 (no edge needed), no cleanup Enable 0
    assert stub.axis.state == STANDSTILL


async def test_found_disabled_with_bit_0_clear_gets_no_0_edge(stub: StubPlc) -> None:
    result = await verb(stub, Verb("enable"))
    assert result.exit_code == 0, result.message
    assert command_writes(stub) == [[ENABLE, 1], [0, 2]]  # Enable 1 at once; only cleanup's Enable 0


# --- GA-U-153.py (review #41): the one-verb mode's words are protocol.md's -----------------------------------------


def _protocol_text() -> str:
    from pathlib import Path

    return " ".join((Path(__file__).resolve().parents[2] / "docs" / "protocol.md").read_text(encoding="utf-8").split())


def test_the_verbs_motion_verbs_and_result_words_match_protocol_md() -> None:
    text = _protocol_text()
    row = re.search(r"\| `--command <verb> \[args\]` .*?\| off \| (.*?) \|", text)
    assert row is not None, "the --command row is missing from protocol.md"
    listed = row.group(1).split("(One-verb mode, below): ", 1)[1].split(". ", 1)
    verbs = re.findall(r"`(\w+)(?: [^`]*)?`", listed[0])
    motion = re.findall(r"`(\w+)`", listed[1].split(" need `--allow-motion`", 1)[0])
    assert verbs, "no verb parsed from the --command row"
    assert motion, "no --allow-motion verb parsed"
    assert tuple(verbs) == VERBS
    assert set(motion) == set(MOTION_VERBS)
    one_verb = text.split("**One-verb mode (`--command`)**", 1)[1].split("### Rules for every run", 1)[0]
    pairs = {word: int(code) for word, code in re.findall(r"`RESULT: (\w+)` \(exit (\d)", one_verb)}
    assert pairs == RESULT_EXIT
    assert len(pairs) == 5, pairs


def test_raw_rounds_half_away_from_zero_as_the_csharp_tool() -> None:
    # Review #37: guards judge the raw value written, rounded half away from zero (0.0625 × 1000 = 62.5 exactly).
    assert (raw(0.0625), raw(-0.0625)) == (63, -63)
    assert (raw(0.0004), raw(-0.0004), raw(500.001)) == (0, 0, 500_001)


async def test_stop_over_a_bit_left_at_1_writes_no_enable_0_and_leaves_c0_as_found(stub: StubPlc) -> None:
    # Review #42 (f52c9e0 cleanup rule, second half): cleanup writes Enable 0 only if THIS run wrote Enable 1. Found
    # Disabled with bit 0 = 1, `stop` keeps the level it found ([Enable|Stop], then the edge clear [Enable]) and never
    # writes Enable 0: not in the verb, not in cleanup. Pinned on the write journal, not the end state.
    pendant_reset(stub)
    result = await verb(stub, Verb("stop"))
    assert (result.result, result.exit_code) == ("PASS", 0), result.message
    words = [values[0] for values in command_writes(stub)]
    assert words == [ENABLE | STOP, ENABLE], words
    assert not any("(Enable 0)" in line for line in result.cleanup), result.cleanup
    assert stub.regs[MAP.command] == ENABLE  # C+0 bit 0 ends 1, as found


# --- GA-U-155.py (review #66): the raw-0 speed message and the dependant phrase are protocol.md's, verbatim ---------


def test_the_speed_refusal_and_the_dependant_phrase_match_protocol_md() -> None:
    text = _protocol_text()
    body = re.search(r"The refusal body is exactly `([^`]+)`", text)
    guard = re.search(r"This guard prints `([^`]+)<body>`", text)
    assert body is not None, "the Speed rounding refusal body is missing from protocol.md"
    assert guard is not None, "the one-verb guard's prefix is missing from protocol.md"
    expected = body.group(1).replace("<pct>", "0.00005").replace("<raw>", "49")
    assert "<" not in expected, expected
    assert speed_zero_body(RegisterMap(), "0.00005", 49) == expected  # a check's SKIP: the body alone
    assert guard.group(1) == SPEED_REFUSAL_PREFIX  # the one-verb guard: prefix + body
    order = re.search(r"`(needs CHK-n, which FAILED)` or `(needs CHK-n, which SKIPPED)`", text)
    assert order is not None, "the Order rule's dependant phrases are missing from protocol.md"
    chk16 = next(c for c in CHECKS if c.id == "CHK-16")
    for result, phrase in ((FAIL, order.group(1)), (SKIPPED, order.group(2))):
        prior = CheckResult("CHK-08", "t", "s", PASS, 0, "m")
        needed = CheckResult("CHK-15", "t", "s", result, 0, "m")
        assert prerequisite_problem(chk16, {"CHK-08": prior, "CHK-15": needed}) == phrase.replace("CHK-n", "CHK-15")


# --- GA-U-156.py (review #68): --speed is printed exactly as typed, wherever a percentage is shown -----------------


@pytest.mark.parametrize(
    ("typed", "max_velocity"),
    [("0.00005", 500_000), ("1e-30", 500_000), ("33.3333333333333333", 1)],  # each rounds to raw Velocity 0
)
async def test_the_speed_is_printed_as_typed_in_the_header_and_the_refusal_body(typed: str, max_velocity: int) -> None:
    # Through the command line: the typed string, never re-rendered from the float (5e-05, 1e-30 → 0.000…1,
    # 33.333333333333336). Numeric parsing serves the guard and the rounding only.
    async with StubPlc(StubOptions(max_velocity=max_velocity)) as plc:
        invocation = parse([f"127.0.0.1:{plc.port}", "--command", "move", "20", "--speed", typed, "--allow-motion"])
        assert invocation.verb is not None
        lines: list[str] = []
        result = await run_verb(invocation.options, invocation.verb, lines.append)
    assert lines[0].startswith(f"--command move 20 --speed {typed} on 127.0.0.1:"), lines[0]
    assert result.message == (
        f"move: Commander/UnreachableSpeed: refused before writing anything: {typed} % of MaxVelocity rounds to raw "
        f"Velocity 0 (round-half-away-from-zero({typed} × {max_velocity} ÷ 100) = 0); nothing to move with. "
        f"Read MaxVelocity (S+12 = input 12) = {max_velocity}."
    )


def test_an_out_of_range_speed_is_printed_as_typed() -> None:
    from generic_axis_check.registers import StatusBlock
    from generic_axis_check.verb import guard_problem

    s = StatusBlock.parse([1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0x9680, 0x98, 0xA120, 7, 1])
    verb = parse(["plc", "--command", "move", "20", "--speed", "150.0", "--allow-motion"]).verb
    assert verb is not None
    assert guard_problem(verb, s, RegisterMap()) == (
        "Commander/UnreachableSpeed: refused before writing anything: speed 150.0 % outside 0 < pct ≤ 100."
    )
