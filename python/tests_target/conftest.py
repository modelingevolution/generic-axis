"""The checklist as pytest integration tests against WHATEVER is connected: the simulator, the Kinco PLC, any PLC.

    GENERIC_AXIS_TARGET=host:port[/unit]   the PLC (port 502 and unit 1 by default)
    GENERIC_AXIS_ALLOW_MOTION=1            also run CHK-12…16 (operator at the machine, travel clear)
    GENERIC_AXIS_SIM_CMD="cmd … {port}"    or --sim: start this simulator on a free port instead of a target

The checklist runs once per session through the same runner as ``python -m generic_axis_check`` (pre-flight, order,
restore, cleanup); each CHK id is then one test. ``report.md`` and ``report.json`` are written as the CLI writes them.
"""

from __future__ import annotations

import asyncio
import os
from pathlib import Path

import pytest

from generic_axis_check.context import Options
from generic_axis_check.report import to_json_text, to_markdown
from generic_axis_check.runner import Report, run
from tests.simproc import Simulator

NOT_CONFIGURED = (
    "no target: set GENERIC_AXIS_TARGET=host:port[/unit], or GENERIC_AXIS_SIM_CMD / --sim '<cmd … {port}>' "
    "to start a simulator"
)


def pytest_addoption(parser: pytest.Parser) -> None:
    parser.addoption("--sim", default=None, help="simulator command with {port}; overrides GENERIC_AXIS_SIM_CMD")
    parser.addoption(
        "--generic-axis-report", default="report.md", help="Markdown report path; JSON is written " "next to it"
    )


def parse_target(text: str) -> tuple[str, int, int]:
    """``host:port[/unit]`` → (host, port, unit)."""
    address, _, unit = text.partition("/")
    host, _, port = address.partition(":")
    return host, int(port) if port else 502, int(unit) if unit else 1


def allow_motion() -> bool:
    return os.environ.get("GENERIC_AXIS_ALLOW_MOTION") == "1"


@pytest.fixture(scope="session")
def conformance(request: pytest.FixtureRequest) -> Report:
    target = os.environ.get("GENERIC_AXIS_TARGET")
    sim_cmd = request.config.getoption("--sim") or os.environ.get("GENERIC_AXIS_SIM_CMD")
    simulator: Simulator | None = None
    if target:
        host, port, unit = parse_target(target)
    elif sim_cmd:
        os.environ["GENERIC_AXIS_SIM_CMD"] = sim_cmd
        simulator = Simulator({})
        host, port, unit = "127.0.0.1", simulator.port, 1
    else:
        pytest.skip(NOT_CONFIGURED)
    try:
        report = asyncio.run(run(Options(host=host, port=port, unit=unit, allow_motion=allow_motion())))
    finally:
        if simulator is not None:
            simulator.stop()
    markdown = Path(request.config.getoption("--generic-axis-report"))
    markdown.write_text(to_markdown(report), encoding="utf-8")
    markdown.with_suffix(".json").write_text(to_json_text(report), encoding="utf-8")
    return report
