"""GA-U-126.py: a budget missed while the simulator missed its cadence is INCONCLUSIVE, never a verdict (lead ruling
"Two-vCPU flakes"). The simulator prints ``simulator: max scan gap <N> ms since the first client connected …`` when
SIGTERM stops it; the stub PLC prints the same line as the test app's --headless."""

from __future__ import annotations

import asyncio
import sys

import pytest

from generic_axis_check.client import PlcClient

from .conftest import PYTHON_DIR
from .simproc import MISSED_CADENCE_ABOVE_MS, Simulator, cadence, inconclusive_reason


def test_only_a_gap_above_50_ms_makes_a_failure_inconclusive() -> None:
    failure = AssertionError("CHK-06 FAIL")
    assert inconclusive_reason(None, failure) is None  # an unknown gap never excuses a failure
    assert inconclusive_reason(MISSED_CADENCE_ABOVE_MS, failure) is None
    assert inconclusive_reason(51, failure) == (
        "INCONCLUSIVE: simulator missed cadence, max scan gap 51 ms (> 50 ms). Budget failure: CHK-06 FAIL"
    )


@pytest.fixture
def stub_simulator(monkeypatch: pytest.MonkeyPatch) -> Simulator:
    monkeypatch.delenv("GENERIC_AXIS_TESTAPP_DLL", raising=False)
    monkeypatch.setenv(
        "GENERIC_AXIS_SIM_CMD", f"{sys.executable} {PYTHON_DIR / 'tests' / 'stub_plc.py'} --port {{port}}"
    )
    return Simulator({})


async def test_the_stub_prints_its_max_scan_gap_on_sigterm(stub_simulator: Simulator) -> None:
    client = PlcClient("127.0.0.1", stub_simulator.port, 1)
    await client.connect()
    try:
        await client.read(114, 1)
        await asyncio.sleep(0.1)  # scans after the client connected
    finally:
        client.close()
    try:
        gap = stub_simulator.max_scan_gap_ms()
    finally:
        stub_simulator.dispose()
    assert gap is not None
    assert gap >= 10  # one 10 ms scan at least


class _Starved(Simulator):
    def __init__(self, gap: int | None) -> None:  # no process: only the gap matters here
        self._gap = gap

    def max_scan_gap_ms(self) -> int | None:
        return self._gap


def _outcome_of_a_failed_budget(gap: int | None) -> str:
    """'fail' or the skip's reason: caught explicitly, so a stray skip can never pass as this test being skipped."""
    try:
        with cadence(_Starved(gap)):
            raise AssertionError("CHK-06 FAIL Machine")
    except AssertionError:
        return "fail"
    except pytest.skip.Exception as skipped:
        return str(skipped)
    return "passed"


def test_cadence_skips_a_failure_only_when_the_simulator_missed_cadence() -> None:
    assert _outcome_of_a_failed_budget(80).startswith("INCONCLUSIVE: simulator missed cadence, max scan gap 80 ms")
    assert _outcome_of_a_failed_budget(12) == "fail"
    assert _outcome_of_a_failed_budget(None) == "fail"  # no exit line: the failure stands
    with cadence(_Starved(500)):
        pass  # a passing test never becomes a skip
