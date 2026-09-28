"""GA-U-60.py — the catalog matches the ``| CHK-nn |`` rows of docs/protocol.md exactly, in order."""

from __future__ import annotations

import re

from generic_axis_check.checks import CHECKS

from .conftest import PROTOCOL

ROW = re.compile(r"^\| (CHK-\d\d) \|")


def protocol_rows() -> list[tuple[str, str, str, tuple[str, ...], bool]]:
    rows = []
    for line in PROTOCOL.read_text(encoding="utf-8").splitlines():
        if not ROW.match(line):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        check_id, title, section, needs = cells[0], cells[1], cells[2], cells[3]
        motion = "--allow-motion" in needs
        ids = tuple(re.findall(r"\b\d\d\b", needs))
        rows.append((check_id, title, section, ids, motion))
    return rows


def test_catalog_ids_titles_sections_and_needs_equal_the_protocol_table() -> None:
    rows = protocol_rows()
    assert len(rows) == 16
    catalog = [(c.id, c.title, c.section, c.needs) for c in CHECKS]
    assert catalog == [(r[0], r[1], r[2], r[3]) for r in rows]


def test_motion_flag_is_the_table_marker_or_inherited_from_a_need() -> None:
    motion: dict[str, bool] = {}
    for check_id, _t, _s, needs, marked in protocol_rows():
        motion[check_id] = marked or any(motion[f"CHK-{n}"] for n in needs)
    assert {c.id: c.motion for c in CHECKS} == motion
