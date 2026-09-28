"""GA-U-62.py — the report follows protocol.md § Report schema."""

from __future__ import annotations

import json
from datetime import UTC, datetime

from generic_axis_check.context import Options
from generic_axis_check.report import to_json, to_json_text, to_markdown
from generic_axis_check.runner import CheckResult, Report

TOP_KEYS = {
    "schema",
    "mapVersion",
    "tool",
    "target",
    "allowMotion",
    "startedAt",
    "finishedAt",
    "summary",
    "checks",
    "cleanup",
}
CHECK_KEYS = {"id", "title", "section", "result", "durationMs", "message", "observed"}


def sample() -> Report:
    at = datetime(2026, 9, 29, 10, 15, 2, tzinfo=UTC)
    return Report(
        Options(host="192.168.58.20"),
        at,
        at,
        [
            CheckResult("CHK-01", "Transport and unit", "Transport", "PASS", 3, "ok", {"roundTripMs": 1}),
            CheckResult(
                "CHK-02", "Map version", "Status block", "FAIL", 1, "MapVersion 2, expected 1", {"mapVersion": 2}
            ),
            CheckResult(
                "CHK-03",
                "Machine limits published",
                'Status block, "Limits come from the machine"',
                "SKIPPED",
                0,
                "needs CHK-02, which FAILED",
            ),
        ],
        ["C+9 = 0 (release lease)"],
    )


def test_json_has_exactly_the_schema_fields() -> None:
    doc = json.loads(to_json_text(sample()))
    assert set(doc) == TOP_KEYS
    assert doc["schema"] == "generic-axis-conformance/1"
    assert doc["mapVersion"] == 1
    assert doc["tool"] == {"name": "generic-axis-check", "language": "python", "version": doc["tool"]["version"]}
    assert doc["target"] == {"host": "192.168.58.20", "port": 502, "unit": 1, "commandBase": 0, "statusBase": 100}
    assert doc["summary"] == {"result": "FAIL", "pass": 1, "fail": 1, "skipped": 1}
    assert doc["startedAt"] == "2026-09-29T10:15:02Z"
    assert all(set(c) == CHECK_KEYS for c in doc["checks"])
    assert doc["checks"][2]["observed"] == {}
    assert doc["cleanup"] == ["C+9 = 0 (release lease)"]


def test_summary_is_pass_when_nothing_failed() -> None:
    report = sample()
    report.checks.pop(1)
    assert to_json(report)["summary"]["result"] == "PASS"
    assert report.exit_code == 0


def test_markdown_has_the_table_columns_cleanup_and_ends_with_the_result() -> None:
    text = to_markdown(sample())
    lines = text.rstrip("\n").splitlines()
    assert lines[0].startswith("# generic-axis-check")
    assert "192.168.58.20:502" in lines[0]
    assert "2026-09-29T10:15:02Z" in lines[0]
    assert "| Id | Title | Result | Observed | Protocol section |" in lines
    assert "- C+9 = 0 (release lease)" in lines
    assert "| CHK-02 | Map version | FAIL | MapVersion 2, expected 1; mapVersion=2 | Status block |" in lines
    assert lines[-1] == "RESULT: FAIL"


def test_exit_code_is_3_when_refused() -> None:
    report = sample()
    report.refused = True
    assert report.exit_code == 3
