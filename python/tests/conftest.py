"""Shared fixtures: the in-process stub PLC (unit tests) and a simulator process (integration tests)."""

from __future__ import annotations

import asyncio
from collections.abc import AsyncIterator, Callable, Iterator
from pathlib import Path

import pytest

from .simproc import Simulator, simulator_command
from .stub_plc import StubOptions, StubPlc

PYTHON_DIR = Path(__file__).resolve().parents[1]
REPO_ROOT = PYTHON_DIR.parent
PROTOCOL = REPO_ROOT / "docs" / "protocol.md"


@pytest.fixture
async def stub() -> AsyncIterator[StubPlc]:
    async with StubPlc() as plc:
        yield plc


@pytest.fixture
def simulator() -> Iterator[Callable[..., Simulator]]:
    if simulator_command(0) is None:
        pytest.skip("set GENERIC_AXIS_TESTAPP_DLL (the built C# test app) or GENERIC_AXIS_SIM_CMD")
    started: list[Simulator] = []

    def start(**overrides: str) -> Simulator:
        sim = Simulator(overrides)
        started.append(sim)
        return sim

    yield start
    for sim in started:
        sim.dispose()


def stub_options(**changes: object) -> StubOptions:
    options = StubOptions()
    for key, value in changes.items():
        setattr(options, key, value)
    return options


async def settle() -> None:
    """Let the stub run one scan."""
    await asyncio.sleep(0.02)
