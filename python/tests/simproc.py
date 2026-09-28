"""Start a headless simulator process on a free port: the C# test app or any ``GENERIC_AXIS_SIM_CMD``."""

from __future__ import annotations

import os
import shlex
import socket
import subprocess
import time

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


class Simulator:
    """One headless simulator process on a free port, configured by environment overrides."""

    def __init__(self, overrides: dict[str, str]) -> None:
        self.port = free_port()
        command = simulator_command(self.port)
        assert command is not None
        env = {**os.environ, **overrides}
        self.process = subprocess.Popen(command, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        wait_for_port(self.port, self.process, SIM_START_TIMEOUT_S)

    def stop(self) -> None:
        self.process.terminate()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
