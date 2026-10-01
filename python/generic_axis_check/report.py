"""The JSON and Markdown reports of protocol.md § Report schema (``generic-axis-conformance/1``)."""

from __future__ import annotations

import json
from datetime import datetime
from importlib.metadata import PackageNotFoundError, version
from typing import Any

from .checks import FAIL, PASS, SKIPPED
from .dump import render
from .registers import COMMAND_LENGTH, MAP_VERSION, STATUS_LENGTH, RegisterMap
from .runner import CheckResult, Report

SCHEMA = "generic-axis-conformance/1"
TOOL_NAME = "generic-axis-check"
LANGUAGE = "python"


def tool_version() -> str:
    try:
        return version(TOOL_NAME)
    except PackageNotFoundError:
        return "source"  # run from a checkout without `pip install`


def _utc(moment: datetime) -> str:
    return moment.strftime("%Y-%m-%dT%H:%M:%SZ")


def to_json(report: Report) -> dict[str, Any]:
    """Exactly the schema's fields, camelCase."""
    o = report.options
    return {
        "schema": SCHEMA,
        "mapVersion": MAP_VERSION,
        "tool": {"name": TOOL_NAME, "language": LANGUAGE, "version": tool_version()},
        "target": {
            "host": o.host,
            "port": o.port,
            "unit": o.unit,
            "commandBase": o.command_base,
            "statusBase": o.status_base,
        },
        "allowMotion": o.allow_motion,
        "startedAt": _utc(report.started_at),
        "finishedAt": _utc(report.finished_at),
        "preflight": report.preflight,  # null unless pre-flight had something to say (the Markdown's line after the heading)
        "summary": {
            "result": report.result,
            "pass": sum(c.result == PASS for c in report.checks),
            "fail": sum(c.result == FAIL for c in report.checks),
            "skipped": sum(c.result == SKIPPED for c in report.checks),
        },
        "checks": [_check(c) for c in report.checks],
        "cleanup": list(report.cleanup),
    }


def _check(c: CheckResult) -> dict[str, Any]:
    entry: dict[str, Any] = {
        "id": c.id,
        "title": c.title,
        "section": c.section,
        "result": c.result,
        "durationMs": c.duration_ms,
        "message": c.message,
        "errorClass": str(c.error_class) if c.result == FAIL and c.error_class is not None else None,
        "observed": dict(c.observed),
    }
    if c.result == FAIL:
        # "A FAIL also carries lastRead" (protocol.md § Report schema); null marks a register never read.
        last = c.last_read
        entry["lastRead"] = {
            "command": list(last.command) if last else [None] * COMMAND_LENGTH,
            "status": list(last.status) if last else [None] * STATUS_LENGTH,
        }
    return entry


def to_json_text(report: Report) -> str:
    return json.dumps(to_json(report), indent=2) + "\n"


def _cell(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ")


def to_markdown(report: Report) -> str:
    """Heading (tool, target, UTC time) · table ``Id | Title | Result | Observed | Protocol section`` · cleanup ·
    ``RESULT: PASS|FAIL|INTERRUPTED|REFUSED`` as the last line."""
    o = report.options
    lines = [
        f"# {TOOL_NAME} ({LANGUAGE} {tool_version()}) — {o.host}:{o.port} unit {o.unit} "
        f"(holding C={o.command_base}, input S={o.status_base}) — {_utc(report.started_at)}",
        "",
        *([f"Pre-flight: {report.preflight}", ""] if report.preflight else []),
        f"Motion checks: {'allowed' if o.allow_motion else 'not allowed (CHK-12…16 SKIPPED)'}.",
        "",
        "| Id | Title | Result | Observed | Protocol section |",
        "|---|---|---|---|---|",
    ]
    for c in report.checks:
        observed = ", ".join(f"{k}={v}" for k, v in c.observed.items())
        detail = c.message if not observed else f"{c.message}; {observed}"
        lines.append(f"| {c.id} | {_cell(c.title)} | {c.result} | {_cell(detail)} | {_cell(c.section)} |")
    failures = [c for c in report.checks if c.result == FAIL]
    if failures:
        # "Each FAIL is followed by its message and the decoded dump of lastRead" (protocol.md § Report schema).
        registers = RegisterMap(o.command_base, o.status_base)
        lines += ["", "Failures:"]
        for c in failures:
            last = c.last_read
            dump = render(registers, last.command, last.status) if last else "no register was read"
            lines += ["", f"{c.id} {c.title}: {c.message}", "", "```", dump, "```"]
    lines += ["", "Cleanup:"]
    lines += [f"- {entry}" for entry in report.cleanup] or ["- nothing to undo"]
    lines += ["", f"RESULT: {report.result}"]
    return "\n".join(lines) + "\n"
