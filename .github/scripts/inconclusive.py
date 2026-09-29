#!/usr/bin/env python3
"""Counts INCONCLUSIVE results in the TRX files of a test run (review #43).

A timing test whose in-process fixture missed its scan cadence reports an xunit skip whose reason
starts with "INCONCLUSIVE": the budget was not measured, and `dotnet test` still exits 0.

  inconclusive.py gate   <results-dir> <expected-trx-count>   release: exit 1 on any INCONCLUSIVE
  inconclusive.py report <results-dir> <expected-trx-count>   CI: write the count to the job summary, exit 0

Fewer TRX files than test projects means the run was not observed. The script never reports that as
zero INCONCLUSIVE: gate exits 1, and report says "not observed" in the summary.
"""
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
PREFIX = "INCONCLUSIVE"


def scan(results: Path):
    trx = sorted(results.rglob("*.trx"))
    found = []
    for f in trx:
        for r in ET.parse(f).getroot().iterfind(".//t:UnitTestResult", NS):
            if r.get("outcome") != "NotExecuted":
                continue
            msg = r.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS).strip()
            if msg.startswith(PREFIX):
                found.append((r.get("testName"), msg))
    return trx, found


def summary(lines):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    text = "\n".join(lines) + "\n"
    if path:
        with open(path, "a", encoding="utf-8") as out:
            out.write(text)
    print(text, end="")


def main():
    mode, results, expected = sys.argv[1], Path(sys.argv[2]), int(sys.argv[3])
    if mode not in ("gate", "report"):
        sys.exit(f"unknown mode {mode!r}: use gate or report")
    trx, found = scan(results) if results.is_dir() else ([], [])

    if len(trx) < expected:
        summary([f"### INCONCLUSIVE: not observed",
                 f"{len(trx)} TRX file(s) under `{results}`, expected {expected} (one per test project). "
                 "No INCONCLUSIVE count is reported for this run."])
        if mode == "gate":
            print(f"::error::INCONCLUSIVE gate did not look: {len(trx)} of {expected} TRX files found under {results}")
            sys.exit(1)
        return

    lines = [f"### INCONCLUSIVE: {len(found)}", f"Scanned {len(trx)} TRX file(s) under `{results}`."]
    lines += [f"- `{name}`: {msg}" for name, msg in found]
    summary(lines)
    if found and mode == "gate":
        print(f"::error::{len(found)} test(s) INCONCLUSIVE: a timing budget was not measured. "
              "A tag is a claim; fix the runner or the fixture, do not re-run to green.")
        sys.exit(1)


if __name__ == "__main__":
    main()
