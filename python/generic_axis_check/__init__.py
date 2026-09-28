"""PLC conformance check for the generic-axis Modbus TCP map (``docs/protocol.md`` § Conformance checks)."""

from .checks import CHECKS, Check, Outcome
from .context import Options
from .runner import CheckResult, Report, run

__all__ = ["CHECKS", "Check", "CheckResult", "Options", "Outcome", "Report", "run"]
