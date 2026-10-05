# generic-axis-check

The PLC conformance check for the generic-axis Modbus TCP map, in Python, for a PLC developer's laptop without .NET.
It runs the checklist of [`docs/protocol.md`](../docs/protocol.md) § Conformance checks (CHK-01…16) against a PLC and
prints a Markdown report. The C# test app's `--check` runs the same checklist with the same ids and report.

Python 3.12 or newer. Install from this repository:

```bash
python3 -m venv .venv && . .venv/bin/activate
pip install ./python
```

## Three commands

Against the simulator (the C# test app, started headless on port 5020):

```bash
dotnet ModelingEvolution.GenericAxis.TestApp.dll --headless --port 5020 &
python -m generic_axis_check 127.0.0.1:5020 --allow-motion --report sim.md
```

Against a PLC, without motion (CHK-01…11; CHK-06 energises the drive but commands no motion):

```bash
python -m generic_axis_check 192.168.58.20 --unit 1 --report plc.md
```

Against a PLC, with motion (CHK-12…16 home, move, stop, jog and kill the beat). **Only with an operator at the
machine and the travel clear:**

```bash
python -m generic_axis_check 192.168.58.20 --allow-motion --report plc-motion.md
```

When a check fails, start with the register dump. It reads both blocks, decodes every register by name, writes
nothing and takes no lease:

```bash
python -m generic_axis_check 192.168.58.20 --dump            # once
python -m generic_axis_check 192.168.58.20 --dump --watch    # at 5 Hz until Ctrl-C
```

Every FAIL says what was seen, in the protocol's shape (`docs/protocol.md` § Errors and debugging), for example
`Protocol/NotAcknowledged: Enable 1 not accepted. CommandSeq 7 written, CommandAck 6 read after 500 ms, State 0
read.` The report attaches the dump of the last read of both blocks to each FAIL.

`--report x.md` also writes `x.json`; `--report x.json` writes JSON only. Other options: `--command-base N`,
`--status-base N` (block bases, both default 0: the command block is holding registers, read by FC03 and written by
FC06/FC16; the status block is input registers, read by FC04), `--owner-id N` (default 65535), `--tolerance X` (CHK-13, axis
units, default 0.1).

Exit codes: 0 no FAIL · 1 at least one FAIL · 2 usage error · 3 refused to start, because another commander (rw2, a
station, or a second tool) is beating. Stop it first; the report says `RESULT: REFUSED` · 4 interrupted. Ctrl-C stops the run, stops the axis, drops
Enable and releases the lease; the running check and the rest are reported SKIPPED.

## One verb at a time (commissioning)

`--command` sends one verb and prints every status read (State, Flags, ActualPosition, ActualVelocity, FaultCode,
CommandAck) until it completes, then cleans up as a run does (protocol.md "One-verb mode"):

```bash
python -m generic_axis_check 192.168.58.20 --command enable          # proves the handshake; ends Disabled
python -m generic_axis_check 192.168.58.20 --command home --allow-motion
python -m generic_axis_check 192.168.58.20 --command move 1500 --speed 20 --allow-motion   # axis units, % of MaxVelocity
python -m generic_axis_check 192.168.58.20 --command jog -50 --for 2 --allow-motion        # axis units/s; Ctrl-C also ends it
python -m generic_axis_check 192.168.58.20 --command stop
python -m generic_axis_check 192.168.58.20 --command reset
```

Exit codes: 0 completed · 1 the PLC failed it · 2 usage error or a guard refused (nothing written) · 3 refused by
pre-flight · 4 interrupted before the verb completed. The last line is `RESULT: COMPLETED`, `FAIL`, `GUARD`,
`REFUSED` or `INTERRUPTED`.

## The same checklist as pytest tests

`tests_target/` runs the checklist once and reports one pytest test per CHK id, against whatever is connected:

```bash
cd python
GENERIC_AXIS_TARGET=192.168.58.20:502/1 pytest tests_target -v                        # a PLC, no motion
GENERIC_AXIS_TARGET=192.168.58.20 GENERIC_AXIS_ALLOW_MOTION=1 pytest tests_target -v   # with motion
pytest tests_target -v --sim "dotnet /path/ModelingEvolution.GenericAxis.TestApp.dll --headless --port {port}"
```

Without a target every test is skipped with the reason. `report.md` and `report.json` are written like the CLI's.

## Development

```bash
pip install -e "./python[dev]"
cd python && ./verify-code-quality.sh        # black, ruff, mypy --strict, unit tests with coverage
GENERIC_AXIS_TESTAPP_DLL=/path/ModelingEvolution.GenericAxis.TestApp.dll pytest tests -m integration
```

`tests/stub_plc.py` is a minimal PLC for the tool's own unit tests. It is not the acceptance target. The C# simulator
is, in CI (`.github/workflows/conformance.yml`). `GENERIC_AXIS_SIM_CMD="python tests/stub_plc.py --headless --port
{port}"` points the integration tests at the stub for development.
