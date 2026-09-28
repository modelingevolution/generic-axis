"""One test per CHK id of docs/protocol.md § Conformance checks, against the configured target (see conftest)."""

from __future__ import annotations

import re

import pytest

from generic_axis_check.checks import CHECKS, FAIL, SKIPPED, Check
from generic_axis_check.runner import Report

from .conftest import allow_motion

pytestmark = pytest.mark.target


def name(check: Check) -> str:
    return f"{check.id}-{re.sub(r'[^a-z0-9]+', '-', check.title.lower()).strip('-')}"


@pytest.mark.parametrize("check", CHECKS, ids=name)
def test_chk(check: Check, conformance: Report) -> None:
    result = next(r for r in conformance.checks if r.id == check.id)
    if result.result == SKIPPED:
        pytest.skip("motion not allowed" if check.motion and not allow_motion() else result.message)
    if result.result == FAIL:
        observed = ", ".join(f"{k}={v}" for k, v in result.observed.items())
        pytest.fail(f"{check.id} {check.title} [{check.section}]: {result.message}; {observed}", pytrace=False)
