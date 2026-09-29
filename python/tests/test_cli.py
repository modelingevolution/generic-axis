"""GA-U-61.py — the command line parses per protocol.md § Conformance checks, "Command line"."""

from __future__ import annotations

import os
import signal
from datetime import UTC, datetime
from pathlib import Path

import pytest

from generic_axis_check import __main__ as cli
from generic_axis_check.__main__ import UsageError, main, parse
from generic_axis_check.context import Options
from generic_axis_check.runner import Report


def test_parse_host_only_takes_the_protocol_defaults() -> None:
    inv = parse(["plc.local"])
    o = inv.options
    assert (o.host, o.port, o.unit, o.owner_id, o.allow_motion) == ("plc.local", 502, 1, 65535, False)
    assert (o.command_base, o.status_base, o.tolerance) == (0, 100, 0.1)
    assert inv.report_md is None
    assert inv.report_json is None


def test_parse_every_argument() -> None:
    inv = parse(
        [
            "10.0.0.5:5020",
            "--unit",
            "3",
            "--allow-motion",
            "--report",
            "r.md",
            "--command-base",
            "200",
            "--status-base",
            "300",
            "--owner-id",
            "65533",
            "--tolerance",
            "0.05",
        ]
    )
    o = inv.options
    assert (o.host, o.port, o.unit, o.allow_motion) == ("10.0.0.5", 5020, 3, True)
    assert (o.command_base, o.status_base, o.owner_id, o.tolerance) == (200, 300, 65533, 0.05)
    assert inv.report_md == Path("r.md")
    assert inv.report_json == Path("r.json")


def test_parse_json_report_writes_only_json() -> None:
    inv = parse(["plc", "--report", "out/r.json"])
    assert inv.report_md is None
    assert inv.report_json == Path("out/r.json")


@pytest.mark.parametrize(
    "argv",
    [
        ["plc", "--report", "r.txt"],
        ["plc:abc"],
        [":502"],
        ["plc", "--owner-id", "65534"],
        ["plc", "--owner-id", "0"],
        ["plc", "--command-base", "95"],
        ["plc", "--tolerance", "0"],
    ],
)
def test_parse_semantic_errors_raise_usage_error(argv: list[str]) -> None:
    with pytest.raises(UsageError):
        parse(argv)


@pytest.mark.parametrize("argv", [["plc", "--report", "r.txt"], ["plc", "--unit", "abc"], []])
def test_main_usage_errors_exit_2(argv: list[str]) -> None:
    try:
        code = main(argv)
    except SystemExit as exc:  # argparse's own usage errors
        code = int(exc.code or 0)
    assert code == 2


def test_a_ctrl_c_while_the_report_is_written_keeps_the_report_and_its_exit_code(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    # GA-U-121.py (lead ruling on the late Ctrl-C): SIGINT after the run finished must not end in KeyboardInterrupt.
    options = Options(host="127.0.0.1", port=1)
    now = datetime.now(UTC)
    finished = Report(options, now, now, [], [])

    async def fake_run(*_args: object, **_kwargs: object) -> Report:
        return finished

    def to_markdown_under_ctrl_c(report: Report) -> str:
        os.kill(os.getpid(), signal.SIGINT)  # the operator presses Ctrl-C as the report is being written
        return real_to_markdown(report)

    real_to_markdown = cli.to_markdown
    monkeypatch.setattr(cli, "run", fake_run)
    monkeypatch.setattr(cli, "to_markdown", to_markdown_under_ctrl_c)
    handler = signal.getsignal(signal.SIGINT)
    try:
        code = cli.main(["127.0.0.1:1", "--report", str(tmp_path / "r.md")])
    finally:
        signal.signal(signal.SIGINT, handler)
    assert code == 0
    assert (tmp_path / "r.md").read_text(encoding="utf-8").endswith("RESULT: PASS\n")
