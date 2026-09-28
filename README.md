# generic-axis

A generic external axis for RocketWelder over Modbus TCP: a linear track that carries the robot or a rotary
positioner that carries the part, fronted by any PLC that can serve the register map in
[`docs/protocol.md`](docs/protocol.md).

| Project | What |
|---|---|
| `src/ModelingEvolution.GenericAxis` | **NuGet.** The driver: `IModbusChannel` with a priority gate, FR-11 heartbeat + advisory lease, `ModbusLinearAxis : ILinearAxis`, `ModbusRotaryAxis : IRotaryAxis`, `ModbusAxisDevice : ILinearTrack / IPositioner`. Depends on `RocketWelder.SDK.Devices.Motion` and FluentModbus only. |
| `src/ModelingEvolution.GenericAxis.Plugin` | rw2 device plugin (`[RocketWelderPlugin("RocketWelder.Motion.Generic")]`): declares the axis and builds the device from the devices hub's values. |
| `src/ModelingEvolution.GenericAxis.TestApp` | **One app for testing**, three roles: (1) the PLC **simulator** (the register map with physics, watchdog, lease, fault injection); (2) a **driver panel** that connects to the simulator or to a real PLC; (3) the **PLC conformance check** — `--check <host>[:port] [--allow-motion]` runs `docs/protocol.md` as a checklist against a real PLC (transport, word order, command/ack, watchdog trip and re-arm, lease; motion and kill-test only with an operator present) and writes a pass/fail report the PLC developer can act on. The simulator must pass the same check. |
| `python/generic_axis_check/` | **The PLC conformance check in Python** (pymodbus): the same checklist as the C# `--check`, same ids and report, for a PLC developer's laptop without .NET. `python -m generic_axis_check <host>[:port] [--allow-motion] [--report out.md]`. Both checkers must pass against the simulator in CI. |
| `src/ModelingEvolution.GenericAxis.Tests` | Unit tests (lease vectors, heartbeat, packing) and live tests against the in-process simulator (home, move, stop, kill-test). |

Requirements: `docs/epics/epic-065-external-axis-motion/features/feature-007-pamet-track-direct-plc/` in the docs repo.
Reference implementation this generalises: `delta-positioner` + `delta-positioner-sim`.

```bash
dotnet build src/GenericAxis.sln
dotnet test  src/GenericAxis.sln
dotnet run --project src/ModelingEvolution.GenericAxis.TestApp   # http://localhost:5070
```
