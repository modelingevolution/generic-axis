#!/usr/bin/env python3
"""Fail unless every conformance report shows the simulator passing the FULL list.

protocol.md § Conformance checks: "The simulator must pass the full list, motion included, in CI." A checker exits 0
when nothing FAILed, SKIPPED included, so a simulator misconfigured into a SKIP (e.g. a MaxVelocity whose 1 % rounds
to raw 0: CHK-15/16 SKIPPED) would pass CI on the exit code alone. This gate reads each JSON report and requires
summary.result == PASS, summary.fail == 0, summary.skipped == 0 and CHK-01…CHK-16 each PASS.

Usage: assert-full-pass.py REPORT.json [REPORT.json ...]   (exit 0 = every report passes the full list)
"""
import json
import sys

EXPECTED = [f"CHK-{n:02d}" for n in range(1, 17)]


def problems(path: str) -> list[str]:
    try:
        with open(path, encoding="utf-8") as f:
            report = json.load(f)
    except (OSError, ValueError) as e:
        return [f"{path}: unreadable report ({e})"]
    found = []
    summary = report.get("summary", {})
    if summary.get("result") != "PASS":
        found.append(f"{path}: summary.result = {summary.get('result')!r}, expected 'PASS'")
    for key in ("fail", "skipped"):
        if summary.get(key) != 0:
            found.append(f"{path}: summary.{key} = {summary.get(key)!r}, expected 0")
    checks = report.get("checks", [])
    ids = [c.get("id") for c in checks]
    if ids != EXPECTED:
        found.append(f"{path}: checks {ids}, expected {EXPECTED}")
    for c in checks:
        if c.get("result") != "PASS":
            found.append(f"{path}: {c.get('id')} {c.get('result')}: {c.get('message')}")
    return found


def main(paths: list[str]) -> int:
    if not paths:
        print(__doc__.strip().splitlines()[-1], file=sys.stderr)
        return 2
    all_problems = [p for path in paths for p in problems(path)]
    for p in all_problems:
        print(f"::error::{p}")
    if all_problems:
        print(f"The simulator did not pass the full list ({len(all_problems)} problem(s)).")
        return 1
    print(f"The simulator passed CHK-01…CHK-16 in every report: {', '.join(paths)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
