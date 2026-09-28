#!/usr/bin/env bash
# Quality gate (docs/standards/python.md): black, ruff, mypy --strict, unit tests with coverage.
# Run from python/ with the dev extras installed: pip install -e ".[dev]"
set -euo pipefail
cd "$(dirname "$0")"
black --check generic_axis_check tests tests_target
ruff check generic_axis_check tests tests_target
mypy generic_axis_check tests tests_target
pytest tests -m "not integration" --cov=generic_axis_check --cov-report=term-missing --cov-fail-under=55
