"""The JSON and Markdown reports of protocol.md § Report schema (``generic-axis-conformance/1``)."""

from __future__ import annotations

import json
from datetime import datetime
from importlib.metadata import PackageNotFoundError, version
from typing import Any

from .checks import FAIL, PASS, SKIPPED
from .registers import MAP_VERSION
from .runner import Report

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
        "summary": {
            "result": report.result,
            "pass": sum(c.result == PASS for c in report.checks),
            "fail": sum(c.result == FAIL for c in report.checks),
            "skipped": sum(c.result == SKIPPED for c in report.checks),
        },
        "checks": [
            {
                "id": c.id,
                "title": c.title,
                "section": c.section,
                "result": c.result,
                "durationMs": c.duration_ms,
                "message": c.message,
                "observed": dict(c.observed),
            }
            for c in report.checks
        ],
        "cleanup": list(report.cleanup),
    }


def to_json_text(report: Report) -> str:
    return json.dumps(to_json(report), indent=2) + "\n"


def _cell(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ")


def to_markdown(report: Report) -> str:
    """Heading (tool, target, UTC time) · table ``Id | Title | Result | Observed | Protocol section`` · cleanup ·
    ``RESULT: PASS|FAIL`` as the last line."""
    o = report.options
    lines = [
        f"# {TOOL_NAME} ({LANGUAGE} {tool_version()}) — {o.host}:{o.port} unit {o.unit} "
        f"(C={o.command_base}, S={o.status_base}) — {_utc(report.started_at)}",
        "",
        f"Motion checks: {'allowed' if o.allow_motion else 'not allowed (CHK-12…16 SKIPPED)'}.",
        "",
        "| Id | Title | Result | Observed | Protocol section |",
        "|---|---|---|---|---|",
    ]
    for c in report.checks:
        observed = ", ".join(f"{k}={v}" for k, v in c.observed.items())
        detail = c.message if not observed else f"{c.message}; {observed}"
        lines.append(f"| {c.id} | {_cell(c.title)} | {c.result} | {_cell(detail)} | {_cell(c.section)} |")
    lines += ["", "Cleanup:"]
    lines += [f"- {entry}" for entry in report.cleanup] or ["- nothing to undo"]
    lines += ["", f"RESULT: {report.result}"]
    return "\n".join(lines) + "\n"
