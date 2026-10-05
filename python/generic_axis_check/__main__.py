"""``python -m generic_axis_check <host>[:port] [--unit N] [--command-base N] [--status-base N] [--owner-id N]
[--allow-motion] [--tolerance X] [--dump [--watch]] [--command <verb> [args] [--speed P] [--for S]] [--report PATH]``
(protocol.md § Conformance checks, "Command line")."""

from __future__ import annotations

import argparse
import asyncio
import logging
import signal
import sys
from collections.abc import Coroutine
from dataclasses import dataclass
from pathlib import Path

from .client import PlcClient, PlcError, PlcRefusedError
from .context import FOREIGN_OWNER_ID, Options
from .dump import dump
from .errors import COMMUNICATION_LOST, PROTOCOL_MISMATCH, ErrorClass, format_message
from .registers import RegisterMap
from .report import to_json_text, to_markdown
from .runner import Report, run
from .verb import DEFAULT_SPEED_PERCENT, MOTION_VERBS, VERBS, VERBS_WITH_VALUE, Verb, VerbResult, run_verb

DEFAULT_PORT = 502
USAGE_ERROR = 2
"""protocol.md: exit code 2 = usage error (argparse's own code)."""
INTERRUPTED_EXIT = 4
"""protocol.md: exit code 4 = interrupted by the operator."""

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
    verb: Verb | None = None


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
    p.add_argument("--command-base", type=_u16, default=0, help="command block base C, holding registers (default 0)")
    p.add_argument("--status-base", type=_u16, default=0, help="status block base S, input registers (default 0)")
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
    p.add_argument(
        "--command",
        nargs="+",
        metavar="VERB",
        help=f"run one verb instead of the checks: {', '.join(VERBS[:5])}, move <target>, jog <signed velocity>",
    )
    p.add_argument("--speed", type=float, help="move: percent of MaxVelocity (default 10)")
    p.add_argument("--for", dest="run_for", type=float, help="jog: end the jog after this many seconds")
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
    verb = _verb(a)
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
    return Invocation(options, report_md, report_json, dump=a.dump, watch=a.watch, verb=verb)


def _verb(a: argparse.Namespace) -> Verb | None:
    """protocol.md § Command line, ``--command <verb> [args]`` and "One-verb mode" (ADR-38): usage errors exit 2."""
    if a.command is None:
        if a.speed is not None or a.run_for is not None:
            raise UsageError("--speed and --for apply to --command move and --command jog")
        return None
    name, *args = a.command
    if name not in VERBS:
        raise UsageError(f"--command {name}: the verb must be one of {', '.join(VERBS)}")
    if a.dump or a.report is not None:
        raise UsageError("--command runs one verb and writes no report: not with --dump or --report")
    if name in MOTION_VERBS and not a.allow_motion:
        raise UsageError(f"--command {name} moves the axis: it needs --allow-motion")
    wanted = 1 if name in VERBS_WITH_VALUE else 0
    if len(args) != wanted:
        shape = {"move": "move <target>", "jog": "jog <signed velocity>"}.get(name, name)
        raise UsageError(f"--command {shape}: got {' '.join(a.command)}")
    value: float | None = None
    if args:
        try:
            value = float(args[0])
        except ValueError as exc:
            raise UsageError(f"--command {name} {args[0]}: not a number") from exc
    if name == "jog" and value == 0:
        raise UsageError("--command jog 0: the velocity must not be 0")
    if a.speed is not None and name != "move":
        raise UsageError("--speed applies to --command move")
    if a.speed is not None and not 0 < a.speed <= 100:
        raise UsageError(f"--speed {a.speed:g}: must be 0 < pct ≤ 100")
    if a.run_for is not None and name != "jog":
        raise UsageError("--for applies to --command jog")
    if a.run_for is not None and a.run_for <= 0:
        raise UsageError(f"--for {a.run_for:g}: must be > 0")
    speed = DEFAULT_SPEED_PERCENT if a.speed is None else a.speed
    return Verb(name, value, speed, a.run_for)


async def run_dump(options: Options, watch: bool) -> int:
    """Rule 4: exit 0 when both blocks were read (and on Ctrl-C while watching), 1 on a Transport error."""
    registers = RegisterMap(options.command_base, options.status_base)
    client = PlcClient(options.host, options.port, options.unit, registers)
    try:
        await client.connect()
        await dump(client, registers, lambda text: print(text, flush=True), watch=watch)
        return 0
    except PlcRefusedError as exc:  # ADR-37: the PLC answered with exception 01/02/03
        print(format_message(ErrorClass.PROTOCOL, PROTOCOL_MISMATCH, str(exc), registers), file=sys.stderr)
        return 1
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


async def _first_ctrl_c_cancels(work: Coroutine[object, object, None]) -> None:
    """Review #26: the first Ctrl-C cancels the work (its cleanup still runs); any further Ctrl-C only says so.
    asyncio.run's own handler raises KeyboardInterrupt on the second one, which aborted cleanup mid-way."""
    loop = asyncio.get_running_loop()
    task = asyncio.current_task()
    interrupts = 0

    def on_sigint() -> None:
        nonlocal interrupts
        interrupts += 1
        if interrupts == 1 and task is not None:
            task.cancel()
        else:
            print("Ctrl-C again: the cleanup is still running and will finish", file=sys.stderr)

    loop.add_signal_handler(signal.SIGINT, on_sigint)
    try:
        await work
    finally:
        loop.remove_signal_handler(signal.SIGINT)
        signal.signal(signal.SIGINT, signal.SIG_IGN)


def run_one_verb(options: Options, verb: Verb) -> int:
    """protocol.md "One-verb mode": print every status read, the verdict, the cleanup and ``RESULT: …``."""

    def out(line: str) -> None:
        print(line, flush=True)

    target = f"{options.host}:{options.port} unit {options.unit}"
    print(f"generic-axis-check --command {verb} on {target}", file=sys.stderr, flush=True)
    produced: list[VerbResult] = []

    async def one() -> None:
        produced.append(await run_verb(options, verb, out))

    try:
        asyncio.run(_first_ctrl_c_cancels(one()))
    except KeyboardInterrupt:
        if not produced:
            print("interrupted before the verb's cleanup finished", file=sys.stderr)
            return INTERRUPTED_EXIT
    finally:
        signal.signal(signal.SIGINT, signal.SIG_IGN)
    result = produced[0]
    out(result.message)
    for line in result.cleanup:
        out(f"cleanup: {line}")
    out(f"RESULT: {result.result}")
    return result.exit_code


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
    if invocation.verb is not None:
        return run_one_verb(invocation.options, invocation.verb)
    print(BANNER, file=sys.stderr)
    produced: list[Report] = []

    async def checklist() -> None:
        produced.append(await run(invocation.options, progress=lambda line: print(line, file=sys.stderr, flush=True)))

    try:
        asyncio.run(_first_ctrl_c_cancels(checklist()))
    except KeyboardInterrupt:
        # A Ctrl-C after the run finished (asyncio.run re-raises it once the task is done), or a second Ctrl-C.
        if not produced:
            print("interrupted before a report was produced", file=sys.stderr)
            return INTERRUPTED_EXIT
    finally:
        # Lead ruling (2026-09-29): once the run is over, a late Ctrl-C must not lose the report or its exit code.
        signal.signal(signal.SIGINT, signal.SIG_IGN)
    report = produced[0]
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
