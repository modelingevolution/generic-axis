"""``python -m generic_axis_check <host>[:port] [--unit N] [--command-base N] [--status-base N] [--owner-id N]
[--allow-motion] [--tolerance X] [--dump [--watch]] [--report PATH]`` (protocol.md § Conformance checks, "Command
line")."""

from __future__ import annotations

import argparse
import asyncio
import logging
import sys
from dataclasses import dataclass
from pathlib import Path

from .client import PlcClient, PlcError
from .context import FOREIGN_OWNER_ID, Options
from .dump import dump
from .errors import COMMUNICATION_LOST, ErrorClass, format_message
from .registers import RegisterMap
from .report import to_json_text, to_markdown
from .runner import run

DEFAULT_PORT = 502
USAGE_ERROR = 2
"""protocol.md: exit code 2 = usage error (argparse's own code)."""

BANNER = (
    "generic-axis-check: CHK-06 energises the drive (no motion is commanded without --allow-motion).\n"
    "--allow-motion moves the axis: only with an operator at the machine and the travel clear."
)


class UsageError(Exception):
    pass


@dataclass(frozen=True, slots=True)
class Invocation:
    options: Options
    report_md: Path | None
    report_json: Path | None
    dump: bool = False
    watch: bool = False


def _u16(text: str) -> int:
    value = int(text)
    if not 0 <= value <= 0xFFFF:
        raise argparse.ArgumentTypeError(f"{value} is outside 0..65535")
    return value


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="python -m generic_axis_check",
        description="Run the generic-axis PLC conformance checklist (docs/protocol.md).",
    )
    p.add_argument("target", metavar="host[:port]", help=f"the PLC; port {DEFAULT_PORT} by default")
    p.add_argument("--unit", type=_u16, default=1, help="Modbus unit id (default 1)")
    p.add_argument("--command-base", type=_u16, default=0, help="command block base C (default 0)")
    p.add_argument("--status-base", type=_u16, default=100, help="status block base S (default 100)")
    p.add_argument("--owner-id", type=_u16, default=65535, help="the checker's lease id (default 65535)")
    p.add_argument(
        "--allow-motion",
        action="store_true",
        help="run CHK-12…16. Only with an operator at the machine and the travel clear",
    )
    p.add_argument("--tolerance", type=float, default=0.1, help="CHK-13 position tolerance in axis units")
    p.add_argument(
        "--dump",
        action="store_true",
        help="print the decoded register dump and run no checks; writes nothing, takes no lease",
    )
    p.add_argument("--watch", action="store_true", help="with --dump: repeat at 5 Hz until Ctrl-C")
    p.add_argument("--report", type=Path, help="*.md: Markdown there plus JSON next to it; *.json: JSON only")
    return p


def parse(argv: list[str]) -> Invocation:
    """Parse per the protocol. argparse exits 2 by itself; semantic errors raise ``UsageError`` (also exit 2)."""
    a = build_parser().parse_args(argv)
    host, _, port_text = a.target.rpartition(":") if ":" in a.target else (a.target, "", "")
    if not host:
        raise UsageError(f"no host in {a.target!r}")
    try:
        port = int(port_text) if port_text else DEFAULT_PORT
    except ValueError as exc:
        raise UsageError(f"port {port_text!r} is not a number") from exc
    if not 1 <= port <= 0xFFFF:
        raise UsageError(f"port {port} is outside 1..65535")
    if a.owner_id in (0, FOREIGN_OWNER_ID):
        raise UsageError(f"--owner-id {a.owner_id}: 0 means unowned and {FOREIGN_OWNER_ID} is CHK-11's foreign id")
    if a.tolerance <= 0:
        raise UsageError("--tolerance must be > 0")
    if a.watch and not a.dump:
        raise UsageError("--watch needs --dump")
    if a.dump and a.report is not None:
        raise UsageError("--report applies to a check run, not to --dump")
    report_md = report_json = None
    if a.report is not None:
        match a.report.suffix.lower():
            case ".md":
                report_md, report_json = a.report, a.report.with_suffix(".json")
            case ".json":
                report_json = a.report
            case _:
                raise UsageError(f"--report {a.report}: the extension must be .md or .json")
    options = Options(
        host=host,
        port=port,
        unit=a.unit,
        command_base=a.command_base,
        status_base=a.status_base,
        owner_id=a.owner_id,
        allow_motion=a.allow_motion,
        tolerance=a.tolerance,
    )
    try:
        RegisterMap(options.command_base, options.status_base)
    except ValueError as exc:
        raise UsageError(str(exc)) from exc
    return Invocation(options, report_md, report_json, dump=a.dump, watch=a.watch)


async def run_dump(options: Options, watch: bool) -> int:
    """Rule 4: exit 0 when both blocks were read (and on Ctrl-C while watching), 1 on a Transport error."""
    registers = RegisterMap(options.command_base, options.status_base)
    client = PlcClient(options.host, options.port, options.unit, registers)
    try:
        await client.connect()
        await dump(client, registers, lambda text: print(text, flush=True), watch=watch)
        return 0
    except PlcError as exc:
        print(format_message(ErrorClass.TRANSPORT, COMMUNICATION_LOST, str(exc), registers), file=sys.stderr)
        return 1
    except asyncio.CancelledError:
        task = asyncio.current_task()
        if task is not None:
            task.uncancel()
        return 0
    finally:
        client.close()


def main(argv: list[str] | None = None) -> int:
    try:
        invocation = parse(sys.argv[1:] if argv is None else argv)
    except UsageError as exc:
        print(f"usage error: {exc}", file=sys.stderr)
        return USAGE_ERROR
    logging.basicConfig(level=logging.WARNING, stream=sys.stderr, format="%(message)s")
    logging.getLogger("pymodbus").setLevel(logging.CRITICAL)  # the checker reports every failure itself

    if invocation.dump:
        return asyncio.run(run_dump(invocation.options, invocation.watch))
    print(BANNER, file=sys.stderr)
    report = asyncio.run(run(invocation.options, progress=lambda line: print(line, file=sys.stderr, flush=True)))
    markdown = to_markdown(report)
    sys.stdout.write(markdown)
    sys.stdout.flush()
    if invocation.report_md is not None:
        invocation.report_md.write_text(markdown, encoding="utf-8")
    if invocation.report_json is not None:
        invocation.report_json.write_text(to_json_text(report), encoding="utf-8")
    return report.exit_code


if __name__ == "__main__":
    sys.exit(main())
