"""GA-I-30.py … GA-I-39: ``python -m generic_axis_check`` as a subprocess against a headless simulator process.

CI starts the built C# test app (``GENERIC_AXIS_TESTAPP_DLL``) with the scenario's options and faults as environment
overrides (test-scenarios.md § Conformance checker against the simulator). ``GENERIC_AXIS_SIM_CMD`` can point the
same tests at another simulator, such as ``tests/stub_plc.py``, for development.
"""

from __future__ import annotations

import asyncio
import json
import os
import signal
import subprocess
import sys
import time
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from generic_axis_check.beat import Beater
from generic_axis_check.client import PlcClient
from generic_axis_check.registers import COMMAND_LENGTH, RegisterMap

from .conftest import PYTHON_DIR
from .simproc import Simulator, simulator_cwd

pytestmark = [pytest.mark.integration, pytest.mark.timeout(240)]
MAP = RegisterMap()
START = Callable[..., Simulator]


def checker(port: int, report: Path, *args: str) -> subprocess.Popen[str]:
    return subprocess.Popen(
        [sys.executable, "-m", "generic_axis_check", f"127.0.0.1:{port}", "--report", str(report), *args],
        cwd=PYTHON_DIR,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )


def run_checker(port: int, tmp_path: Path, *args: str) -> tuple[int, dict[str, Any]]:
    report = tmp_path / "report.md"
    process = checker(port, report, *args)
    _out, err = process.communicate(timeout=200)
    assert report.with_suffix(".json").exists(), err
    return process.returncode, json.loads(report.with_suffix(".json").read_text(encoding="utf-8"))


def results(doc: dict[str, Any]) -> dict[str, tuple[str, str]]:
    return {c["id"]: (c["result"], c["message"]) for c in doc["checks"]}


def ids(first: int, last: int) -> list[str]:
    return [f"CHK-{n:02d}" for n in range(first, last + 1)]


async def registers(port: int, address: int, count: int) -> list[int]:
    client = PlcClient("127.0.0.1", port, 1)
    await client.connect()
    try:
        return await client.read(address, count)
    finally:
        client.close()


async def test_ga_i_30_simulator_passes_the_whole_checklist(simulator: START, tmp_path: Path) -> None:
    sim = simulator()
    code, doc = run_checker(sim.port, tmp_path, "--allow-motion")
    assert {k: v for k, v in results(doc).items() if v[0] != "PASS"} == {}
    assert code == 0
    observed = {c["id"]: c["observed"] for c in doc["checks"]}
    assert 1000 <= observed["CHK-08"]["tripAfterMs"] <= 1500
    assert observed["CHK-14"]["haltMs"] <= 200
    state = (await registers(sim.port, MAP.status, 1))[0]
    lease, fault = await registers(sim.port, MAP.lease_owner, 2)
    assert (state, lease, fault) == (0, 0, 0)


async def test_ga_i_31_motion_checks_are_opt_in(simulator: START, tmp_path: Path) -> None:
    sim = simulator()
    before = await registers(sim.port, MAP.status + 2, 2)
    code, doc = run_checker(sim.port, tmp_path)
    r = results(doc)
    assert [r[i][0] for i in ids(1, 11)] == ["PASS"] * 11
    assert [r[i] for i in ids(12, 16)] == [("SKIPPED", "needs --allow-motion")] * 5
    assert code == 0
    assert await registers(sim.port, MAP.status + 2, 2) == before


async def test_ga_i_32_wrong_map_version_stops_the_run(simulator: START, tmp_path: Path) -> None:
    sim = simulator(Simulator__MapVersion="2")
    before = await registers(sim.port, MAP.command, COMMAND_LENGTH)
    code, doc = run_checker(sim.port, tmp_path, "--allow-motion")
    r = results(doc)
    assert r["CHK-01"][0] == "PASS"
    assert r["CHK-02"] == (
        "FAIL",
        "Protocol/ProtocolMismatch: MapVersion not 1. Read MapVersion (S+14 = 114) = 2, expected 1.",
    )
    chk02 = doc["checks"][1]
    assert chk02["errorClass"] == "Protocol"
    assert chk02["lastRead"]["status"][14] == 2
    assert all(r[i][0] == "SKIPPED" for i in ids(3, 16))
    assert code == 1
    assert await registers(sim.port, MAP.command, COMMAND_LENGTH) == before


async def test_ga_i_33_unpublished_limits_fail_chk03_only(simulator: START, tmp_path: Path) -> None:
    sim = simulator(Simulator__PublishLimits="false")
    code, doc = run_checker(sim.port, tmp_path, "--allow-motion")
    r = results(doc)
    assert r["CHK-03"] == (
        "FAIL",
        "Protocol/ProtocolMismatch: limits not published (all zero). Read TravelMin (S+8 = 108) = 0, "
        "TravelMax (S+10 = 110) = 0, MaxVelocity (S+12 = 112) = 0.",
    )
    assert all(r[i][0] == "SKIPPED" for i in ids(13, 16))
    assert [r[i][0] for i in ids(4, 12)] == ["PASS"] * 9
    assert code == 1


async def test_ga_i_34_a_plc_without_the_watchdog_is_caught(simulator: START, tmp_path: Path) -> None:
    sim = simulator(Simulator__Faults__WatchdogDisabled="true")
    code, doc = run_checker(sim.port, tmp_path)
    r = results(doc)
    assert r["CHK-08"][0] == "FAIL"
    assert "no trip within 1.5 s of the last beat" in r["CHK-08"][1]
    assert [r[i][0] for i in ("CHK-09", "CHK-10", "CHK-16")] == ["SKIPPED"] * 3
    assert code == 1


async def test_ga_i_35_a_plc_that_never_acknowledges_is_caught(simulator: START, tmp_path: Path) -> None:
    sim = simulator(Simulator__Faults__SuppressAck="true")
    _code, doc = run_checker(sim.port, tmp_path)
    r = results(doc)
    assert r["CHK-06"][0] == "FAIL"
    assert r["CHK-06"][1].startswith("Protocol/NotAcknowledged: Enable 1 not accepted. CommandSeq ")
    assert [r[i][0] for i in ("CHK-07", "CHK-08", "CHK-09", "CHK-10", "CHK-12")] == ["SKIPPED"] * 5


async def test_ga_i_36_swapped_word_order_is_caught(simulator: START, tmp_path: Path) -> None:
    sim = simulator(Simulator__Faults__SwappedWordOrder="true")
    code, doc = run_checker(sim.port, tmp_path)
    chk03 = next(c for c in doc["checks"] if c["id"] == "CHK-03")
    assert chk03["result"] == "FAIL"
    assert list(chk03["observed"]) == ["travelMin", "travelMax", "maxVelocity", "retries"]
    assert chk03["errorClass"] == "Protocol"
    assert code == 1


async def test_ga_i_37_the_checker_never_fights_a_live_commander(simulator: START, tmp_path: Path) -> None:
    sim = simulator()
    commander = PlcClient("127.0.0.1", sim.port, 1)
    await commander.connect()
    await commander.write(MAP.lease_owner, [1])
    beat = Beater(commander, MAP)
    await beat.start()
    try:
        before = await registers(sim.port, MAP.command, 8)
        started = time.monotonic()
        report = tmp_path / "report.md"
        process = checker(sim.port, report, "--allow-motion")
        while process.poll() is None:  # noqa: ASYNC110 — the commander keeps beating on this loop meanwhile
            await asyncio.sleep(0.05)
        elapsed = time.monotonic() - started
        after = await registers(sim.port, MAP.command, 8)
        (lease,) = await registers(sim.port, MAP.lease_owner, 1)
    finally:
        await beat.stop()
        commander.close()
    doc = json.loads(report.with_suffix(".json").read_text(encoding="utf-8"))
    assert process.returncode == 3
    assert doc["summary"]["result"] == "REFUSED"
    assert report.read_text(encoding="utf-8").endswith("\nRESULT: REFUSED\n")
    assert all(c["result"] == "SKIPPED" for c in doc["checks"])
    assert "another commander is live" in doc["checks"][0]["message"]
    assert "LeaseOwner (C+9) 1" in doc["checks"][0]["message"]
    assert elapsed < 10  # "exits after about 1 s" plus interpreter start-up
    assert after[:8] == before[:8]  # Command, CommandSeq and the parameters are untouched
    assert lease == 1


async def test_ga_i_38_interrupting_a_run_cleans_up(simulator: START, tmp_path: Path) -> None:
    sim = simulator()
    report = tmp_path / "report.md"
    process = checker(sim.port, report, "--allow-motion")
    assert process.stderr is not None
    for line in process.stderr:  # CHK-14 starts: its move is commanded within the next scans
        if line.startswith("CHK-14 "):
            break
    process.send_signal(signal.SIGINT)
    process.communicate(timeout=60)
    await asyncio.sleep(0.5)
    status = await registers(sim.port, MAP.status, 6)
    (lease,) = await registers(sim.port, MAP.lease_owner, 1)
    doc = json.loads(report.with_suffix(".json").read_text(encoding="utf-8"))
    assert status[0] in (0, 1)
    assert status[4:6] == [0, 0]  # ActualVelocity
    assert lease == 0
    assert "C+9 = 0 (release lease)" in doc["cleanup"]
    assert process.returncode == 4
    assert doc["summary"]["result"] == "INTERRUPTED"
    r = results(doc)
    assert [r[i] for i in ids(14, 16)] == [("SKIPPED", "interrupted by the operator during CHK-14")] * 3


def test_ga_i_39_both_tools_agree(simulator: START, tmp_path: Path) -> None:
    dll = os.environ.get("GENERIC_AXIS_TESTAPP_DLL")
    if not dll:
        pytest.skip("needs the C# checker: GENERIC_AXIS_TESTAPP_DLL")
    sim = simulator()
    cs_report = tmp_path / "csharp.json"
    cs = subprocess.run(
        ["dotnet", dll, "--check", f"127.0.0.1:{sim.port}", "--allow-motion", "--report", str(cs_report)],
        capture_output=True,
        text=True,
        timeout=200,
        cwd=simulator_cwd(),
        check=False,
    )
    assert cs_report.exists(), cs.stderr
    _code, py = run_checker(sim.port, tmp_path, "--allow-motion")
    csharp = json.loads(cs_report.read_text(encoding="utf-8"))

    def key(doc: dict[str, Any]) -> list[tuple[str, str, str, str]]:
        return [(c["id"], c["title"], c["section"], c["result"]) for c in doc["checks"]]

    assert key(py) == key(csharp)
    assert csharp["tool"]["language"] == "csharp"
