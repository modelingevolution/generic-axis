"""GA-U-60.py — the catalog matches docs/protocol.md exactly, in order: the ``| CHK-nn |`` rows of § The checks
(ids, titles, sections, Needs) and of § Observed values (the key list of each check)."""

from __future__ import annotations

import re

from generic_axis_check.checks import CHECKS, OBSERVED, RETRIES_KEY

from .conftest import PROTOCOL

ROW = re.compile(r"^\| (CHK-\d\d) \|")


def section_rows(heading: str) -> list[list[str]]:
    """The ``| CHK-nn |`` table rows under ``### <heading>``, up to the next heading."""
    rows, inside = [], False
    for line in PROTOCOL.read_text(encoding="utf-8").splitlines():
        if line.startswith("#"):
            inside = line.strip() == f"### {heading}"
            continue
        if inside and ROW.match(line):
            rows.append([c.strip() for c in line.strip().strip("|").split("|")])
    return rows


def check_rows() -> list[tuple[str, str, str, tuple[str, ...], bool]]:
    return [
        (cells[0], cells[1], cells[2], tuple(re.findall(r"\b\d\d\b", cells[3])), "--allow-motion" in cells[3])
        for cells in section_rows("The checks")
    ]


def test_catalog_ids_titles_sections_and_needs_equal_the_protocol_table() -> None:
    rows = check_rows()
    assert len(rows) == 16
    catalog = [(c.id, c.title, c.section, c.needs) for c in CHECKS]
    assert catalog == [(r[0], r[1], r[2], r[3]) for r in rows]


def test_motion_flag_is_the_table_marker_or_inherited_from_a_need() -> None:
    motion: dict[str, bool] = {}
    for check_id, _t, _s, needs, marked in check_rows():
        motion[check_id] = marked or any(motion[f"CHK-{n}"] for n in needs)
    assert {c.id: c.motion for c in CHECKS} == motion


def test_observed_keys_equal_the_protocol_table_in_order() -> None:
    table = {cells[0]: tuple(re.findall(r"`(\w+)`", cells[1])) for cells in section_rows("Observed values")}
    assert list(table) == [c.id for c in CHECKS]
    assert {k: (*v, RETRIES_KEY) for k, v in OBSERVED.items()} == table
