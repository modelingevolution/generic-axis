"""GA-U-154.py — one speed rounding for the checks and the one-verb mode (protocol.md "Speed rounding", review #65)."""

from __future__ import annotations

import pytest

from generic_axis_check.beat import Beater
from generic_axis_check.checks import CHECKS, PASS, SKIPPED
from generic_axis_check.client import PlcClient
from generic_axis_check.context import CheckContext, Options
from generic_axis_check.registers import RegisterMap, speed_raw
from generic_axis_check.runner import run
from generic_axis_check.verb import Verb, move_velocity, run_verb

from .stub_plc import StubOptions, StubPlc

MAP = RegisterMap()
MOVE_VEL = 8


@pytest.mark.parametrize(
    ("max_velocity", "percent", "expected"),
    [
        (49, 1, 0),  # 0.49 → 0: refused, never floored to 1
        (50, 1, 1),  # 0.5 → 1: half away from zero (truncation gives 0)
        (149, 1, 1),  # 1.49 → 1 (reviewer-python-2 #43)
        (150, 1, 2),  # 1.5 → 2 (truncation gives 1)
        (250, 1, 3),  # 2.5 → 3: half away from zero (banker's gives 2)
        (55, 10, 6),  # 5.5 → 6 (truncation gives 5)
        (500_000, 10, 50_000),
        (500_000, 1, 5_000),
    ],
)
def test_speed_raw_rounds_half_away_from_zero_and_never_floors(max_velocity: int, percent: int, expected: int) -> None:
    assert speed_raw(max_velocity, percent) == expected


def stub_options(max_velocity: int) -> StubOptions:
    # Homed 6 raw from CHK-13's target (TravelMin + 10), just outside the stub's 5-raw arrival window: a real move that
    # ends in about 0.2 s even at the 5–6 raw/s of a tiny MaxVelocity.
    return StubOptions(max_velocity=max_velocity, home_position=10_006, initial_position=10_006)


def velocity_params(plc: StubPlc) -> list[int]:
    """Every Velocity (C+4, low word) the checker wrote, from the stub's write journal."""
    return [
        values[2] for address, values in plc.writes if address == MAP.target_position and len(values) == 6
    ]  # C+2…C+7 (CHK-05 writes C+2…C+3 alone)


@pytest.mark.timeout(120)
async def test_a_1_percent_that_rounds_to_raw_0_skips_chk15_writing_nothing_while_the_10_percent_checks_run() -> None:
    async with StubPlc(stub_options(49)) as plc:
        report = await run(Options(host="127.0.0.1", port=plc.port, allow_motion=True))
        writes = list(plc.writes)
    results = {c.id: c for c in report.checks}
    for check_id in ("CHK-12", "CHK-13", "CHK-14"):
        assert results[check_id].result == PASS, (check_id, results[check_id].message)
    assert results["CHK-14"].observed["commandedVelocity"] == 5  # 4.9 → 5
    chk15 = results["CHK-15"]
    assert chk15.result == SKIPPED
    assert chk15.message == (  # the refusal body alone: no class prefix on a SKIP (protocol e49dd62)
        "1 % of MaxVelocity rounds to raw Velocity 0 (round-half-away-from-zero(1 × 49 ÷ 100) = 0); nothing to move "
        "with. Read MaxVelocity (S+12 = input 12) = 49."
    )
    assert chk15.error_class is None
    assert chk15.observed == {}
    # A machine-property skip, not a timing one: the release gate (.github/scripts/inconclusive.py) counts results whose
    # message starts with "INCONCLUSIVE", and tests_target skips with this message verbatim. It never carries the
    # simulator cadence line.
    assert not chk15.message.startswith("INCONCLUSIVE")
    assert "cadence" not in chk15.message
    assert "scan gap" not in chk15.message
    assert results["CHK-16"].result == SKIPPED
    assert results["CHK-16"].message == "needs CHK-15, which SKIPPED"
    # Zero motion writes for the refused speed: no MoveVelocity command ever, no Velocity 0 parameter.
    assert not any(address == MAP.command and values[0] & MOVE_VEL for address, values in writes)
    assert 0 not in velocity_params_from(writes)
    assert report.exit_code == 0


def velocity_params_from(writes: list[tuple[int, list[int]]]) -> list[int]:
    return [
        values[2] for address, values in writes if address == MAP.target_position and len(values) == 6
    ]  # C+2…C+7 (CHK-05 writes C+2…C+3 alone)


@pytest.mark.timeout(120)
async def test_every_check_uses_the_one_rounding() -> None:
    # MaxVelocity 55: 10 % = 5.5 → 6 and 1 % = 0.55 → 1. Truncation would give 5 and 0 at any one call site.
    async with StubPlc(stub_options(55)) as plc:
        report = await run(Options(host="127.0.0.1", port=plc.port, allow_motion=True))
        velocities = velocity_params(plc)
    results = {c.id: c for c in report.checks}
    assert {c.id: c.result for c in report.checks if c.result != PASS} == {}, [
        (c.id, c.message) for c in report.checks if c.result != PASS
    ]
    assert results["CHK-14"].observed["commandedVelocity"] == 6
    assert results["CHK-15"].observed["commandedVelocity"] == 1
    assert results["CHK-16"].observed["commandedVelocity"] == 1
    # CHK-13 reports no commanded velocity: its Velocity parameter is the first one written.
    assert velocities[:2] == [6, 6], velocities  # CHK-13, then CHK-14


def test_the_one_verb_speed_uses_the_same_helper() -> None:
    # --speed 1 on MaxVelocity 250: 2.5 → 3, the checks' rounding.
    from generic_axis_check.registers import StatusBlock

    s = StatusBlock.parse([1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0x9680, 0x98, 250, 0, 1])
    assert move_velocity(s, 1) == 3
    assert move_velocity(s, 0.1) == 0  # 0.25 → 0: the one-verb guard refuses it


async def test_a_one_verb_move_whose_speed_rounds_to_0_writes_nothing() -> None:
    async with StubPlc(stub_options(49)) as plc:
        result = await run_verb(Options(host="127.0.0.1", port=plc.port, allow_motion=True), Verb("move", 20, 1), print)
        writes = list(plc.writes)
    assert (result.result, result.exit_code) == ("GUARD", 2), result.message
    assert result.message == (
        "move: Commander/UnreachableSpeed: refused before writing anything: 1 % of MaxVelocity rounds to raw Velocity 0 "
        "(round-half-away-from-zero(1 × 49 ÷ 100) = 0); nothing to move with. Read MaxVelocity (S+12 = input 12) = 49."
    )  # review #66: the check's SKIP message, verbatim, after the verb
    assert writes == []


@pytest.mark.parametrize(
    ("check_id", "max_velocity"),
    [("CHK-13", 4), ("CHK-14", 4), ("CHK-15", 49), ("CHK-16", 49)],  # 10 % of 4 → 0.4 → 0; 1 % of 49 → 0.49 → 0
)
async def test_a_self_skipping_check_entered_from_disabled_writes_nothing(check_id: str, max_velocity: int) -> None:
    # Review #67: entered from Disabled (where a restore that had to Reset leaves the axis), the speed refusal comes
    # before the energise path: zero client writes during the check, on the write journal. A check that enables first
    # and refuses after would write Enable here; from Standstill it would not, which is why the 49 run cannot show it.
    async with StubPlc(StubOptions(max_velocity=max_velocity)) as plc:
        assert plc.axis.state == 0  # Disabled
        client = PlcClient("127.0.0.1", plc.port, 1)
        await client.connect()
        try:
            ctx = CheckContext(client, MAP, Options(host="127.0.0.1", port=plc.port), Beater(client, MAP))
            check = next(c for c in CHECKS if c.id == check_id)
            before = len(plc.writes)
            outcome = await check.run(ctx)
            during = plc.writes[before:]
        finally:
            client.close()
    assert outcome.result == SKIPPED, outcome.message
    assert during == []
