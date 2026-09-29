"""GA-U-117.py (review #17): tests_target writes each run's report to its own path."""

from __future__ import annotations

from pathlib import Path

import pytest

from tests_target.conftest import report_path


def test_every_run_without_an_option_gets_its_own_report_directory(tmp_path_factory: pytest.TempPathFactory) -> None:
    first = report_path(None, tmp_path_factory)
    second = report_path(None, tmp_path_factory)
    assert first.name == second.name == "report.md"
    assert first.parent != second.parent
    assert first.parent.is_dir()
    assert second.parent.is_dir()


def test_an_explicit_report_path_is_used_as_given(tmp_path_factory: pytest.TempPathFactory) -> None:
    assert report_path("out/r.md", tmp_path_factory) == Path("out/r.md")
