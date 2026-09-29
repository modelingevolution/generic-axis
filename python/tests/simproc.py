"""Start a headless simulator process on a free port: the C# test app or any ``GENERIC_AXIS_SIM_CMD``."""

from __future__ import annotations

import contextlib
import os
import re
import shlex
import socket
import subprocess
import tempfile
import time
from collections.abc import Iterator
from pathlib import Path

import pytest

SIM_START_TIMEOUT_S = 60.0
"""A cold ``dotnet`` start on a CI runner; waiting ends as soon as the port accepts."""


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        port: int = s.getsockname()[1]
        return port


def simulator_command(port: int) -> list[str] | None:
    """The C# test app (``GENERIC_AXIS_TESTAPP_DLL``, design.md CI) or any ``GENERIC_AXIS_SIM_CMD`` with ``{port}``."""
    dll = os.environ.get("GENERIC_AXIS_TESTAPP_DLL")
    if dll:
        return ["dotnet", dll, "--headless", "--port", str(port)]
    template = os.environ.get("GENERIC_AXIS_SIM_CMD")
    if template:
        return shlex.split(template.format(port=port))
    return None


def simulator_cwd() -> str | None:
    """The test app's own directory: a .NET host watches ``appsettings.json`` in its working directory, and that
    watch hangs on a WSL drvfs mount (/mnt/*), so the process never starts listening there."""
    dll = os.environ.get("GENERIC_AXIS_TESTAPP_DLL")
    return os.path.dirname(os.path.abspath(dll)) if dll else None


def wait_for_port(port: int, process: subprocess.Popen[bytes], timeout_s: float) -> None:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f"simulator exited with {process.returncode} before listening on {port}")
        try:
            with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.1)
    raise RuntimeError(f"simulator did not listen on {port} within {timeout_s:g} s")


MISSED_CADENCE_ABOVE_MS = 50
"""A scan gap above this means the simulator missed its cadence (nominal 10 ms); the driver tests use the same bound
(``Cadence.MissedAbove``)."""

MAX_SCAN_GAP_LINE = re.compile(r"^simulator: max scan gap (\d+) ms since the first client connected", re.MULTILINE)
"""The line a headless simulator prints to stdout when SIGTERM stops it (the test app's --headless and
tests/stub_plc.py): ``simulator: max scan gap <N> ms since the first client connected (scan interval <M> ms)``."""


class Simulator:
    """One headless simulator process on a free port, configured by environment overrides. Its stdout goes to a file
    so the exit line with the maximum scan gap can be read after the stop (a pipe could fill and block it)."""

    def __init__(self, overrides: dict[str, str]) -> None:
        self.port = free_port()
        command = simulator_command(self.port)
        assert command is not None
        env = {**os.environ, **overrides}
        self._stdout = tempfile.NamedTemporaryFile(  # noqa: SIM115 — lives as long as the process writing it
            prefix=f"sim-{self.port}-", suffix=".log", delete=False
        )
        self.process = subprocess.Popen(
            command, env=env, cwd=simulator_cwd(), stdout=self._stdout, stderr=subprocess.DEVNULL
        )
        wait_for_port(self.port, self.process, SIM_START_TIMEOUT_S)

    def stop(self) -> None:
        if self.process.poll() is not None:
            return
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()

    def dispose(self) -> None:
        self.stop()
        self._stdout.close()
        Path(self._stdout.name).unlink(missing_ok=True)

    def max_scan_gap_ms(self) -> int | None:
        """Stops the simulator and returns the maximum scan gap it printed on exit; None if it printed none."""
        self.stop()
        self._stdout.flush()
        text = Path(self._stdout.name).read_text(encoding="utf-8", errors="replace")
        found = MAX_SCAN_GAP_LINE.findall(text)
        return int(found[-1]) if found else None


def inconclusive_reason(max_gap_ms: int | None, failure: BaseException) -> str | None:
    """The skip reason when a budget failed while the simulator missed its cadence, else None (the failure stands).
    An unknown gap never makes a failure inconclusive."""
    if max_gap_ms is None or max_gap_ms <= MISSED_CADENCE_ABOVE_MS:
        return None
    return (
        f"INCONCLUSIVE: simulator missed cadence, max scan gap {max_gap_ms} ms (> {MISSED_CADENCE_ABOVE_MS} ms). "
        f"Budget failure: {failure}"
    )


@contextlib.contextmanager
def cadence(sim: Simulator) -> Iterator[None]:
    """Around the assertions of a test with a timing budget: a failure while the simulator missed its cadence is
    SKIPPED as INCONCLUSIVE (the fixture, not the PLC under test, broke the budget); otherwise it stands. Nothing is
    skipped before the budget was measured, and a passing test never looks at the gap."""
    try:
        yield
    except AssertionError as failure:
        reason = inconclusive_reason(sim.max_scan_gap_ms(), failure)
        if reason is None:
            raise
        pytest.skip(reason)
