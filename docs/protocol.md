# Generic external axis over Modbus TCP — protocol

One register map for any external axis a PLC fronts: a linear track carrying the robot (Pamet: Kinco PLC → Lichuan
servo), a rotary positioner carrying the part, or any drive behind a PLC that can serve Modbus TCP. rw2 owns the
axis through this map alone. The robot controller's own external-axis coupling is never used (owner ruling
2026-09-28/29). The map is the contract between the driver in this repository and the PLC program; both sides are
written against this file.

Lineage: epic-065 external-axis-motion (Delta VFD-C2000 positioner, `delta-positioner` `DeltaRegisters.cs`,
FR-11 dead-commander watchdog) and epic-065 feature-007 (Pamet track directly from the Kinco PLC).

## Transport

| Item | Value |
|---|---|
| Transport | Modbus TCP, port 502 (configurable) |
| Unit id | 1 (configurable) |
| Register type | Holding registers only (FC03 read, FC06/FC16 write). No coils: one register file is simpler to map in every PLC. |
| Block placement | Two blocks. **Command block** at base `C` (default **0**), **status block** at base `S` (default **100**). Offsets inside a block are fixed; only the two bases are configurable, on both sides, for a PLC whose register file cannot start at 0/100. The addresses below are written for the defaults. |
| Word order for 32-bit values | **low word first** (register n = bits 0–15, register n+1 = bits 16–31), two's complement; each 16-bit register big-endian on the wire (Modbus standard) |
| Position unit | 0.001 mm for a linear axis, 0.001° for a rotary axis (the driver's configured kind decides; the map is the same) |
| Velocity unit | 0.001 unit/s; acceleration 0.001 unit/s² |
| Consistency | The PLC answers one FC03 read of the whole status block (15 registers) from **one scan's image**, and applies one FC16 write before or after a scan, never in the middle of one. |
| Map version | register 114 = **1** for this document |

## Command block — written by the driver, read by the PLC (holding registers C+0 … C+11)

| Addr | Name | Type | Meaning |
|---|---|---|---|
| 0 | `Command` | bitfield | bit 0 **Enable** (level: 1 = servo on). bit 1 **Home**, bit 2 **MoveAbsolute**, bit 3 **MoveVelocity**, bit 4 **Stop**, bit 5 **Reset** — edge-triggered: the PLC acts on 0→1 and the driver clears the bit after `CommandAck` echoes `CommandSeq`. Stop has priority over every other bit in the same word. Bits 6–15 are 0. |
| 1 | `CommandSeq` | uint16 | Incremented by the driver with every **command write** — a write that sets an edge bit or changes Enable (never 0; 65535 wraps to 1). The write that only clears edge bits after an ack does **not** increment it. |
| 2–3 | `TargetPosition` | int32 | Target for MoveAbsolute, in position units. |
| 4–5 | `Velocity` | int32 | Speed for MoveAbsolute (magnitude) and MoveVelocity (signed: sign = direction). |
| 6–7 | `Acceleration` | int32 | Ramp for the next move; 0 = PLC default. |
| 8 | `Heartbeat` | uint16 | FR-11: the driver writes an incrementing value (1…65535, never 0) at ≥ 5 Hz for the whole connection lifetime. The driver's default is 10 Hz. |
| 9 | `LeaseOwner` | uint16 | FR-11 advisory lease: 0 = unowned; else the station-unique owner id of the driver holding the axis. |
| 10 | `WatchdogFault` | uint16 | 0 healthy; 1 = the PLC tripped the watchdog. **Cleared only by the driver writing 0.** Written by the PLC on trip. |
| 11 | `WatchdogTrips` | uint16 | Trip counter since PLC power-up. PLC-owned, read-only for the driver. |

## Status block — written by the PLC, read by the driver (holding registers S+0 … S+14)

| Addr | Name | Type | Meaning |
|---|---|---|---|
| 100 | `State` | uint16 | 0 Disabled · 1 Standstill · 2 Homing · 3 DiscreteMotion · 4 ContinuousMotion · 5 (reserved) · 6 Stopping · 7 ErrorStop — the numbers of the SDK `AxisState` enum. Any other value is read by the driver as ErrorStop. |
| 101 | `Flags` | bitfield | bit 0 Homed · bit 1 InPosition · bit 2 LimitMin · bit 3 LimitMax · bit 4 HomeSensor · bit 5 DriveReady · bit 6 Moving |
| 102–103 | `ActualPosition` | int32 | Position units. Valid only while `Homed` is set (an absolute encoder keeps it across power cycles; the map does not care which). |
| 104–105 | `ActualVelocity` | int32 | Signed, 0.001 unit/s. |
| 106 | `FaultCode` | uint16 | 0 none · 1 drive fault · 2 limit switch tripped · 3 following error · 4 watchdog · 5 homing failed · 6 communication to drive lost · 7 safety stop (E-stop chain, guard) · 100+ vendor-specific (documented per PLC). |
| 107 | `CommandAck` | uint16 | Echo of the last accepted `CommandSeq`. |
| 108–109 | `TravelMin` | int32 | Machine limit, published by the PLC. The driver refuses targets outside `TravelMin..TravelMax`; it never clamps. |
| 110–111 | `TravelMax` | int32 | |
| 112–113 | `MaxVelocity` | int32 | Machine limit, published by the PLC. The driver refuses faster speeds. |
| 114 | `MapVersion` | uint16 | 1. The driver reads the status block before its first write and refuses to attach to any other value, writing nothing. |

Limits come from the machine through this block. If a PLC cannot publish them (`TravelMin`, `TravelMax` and
`MaxVelocity` all zero), the driver uses the values from its own configuration and logs that it did; it never
invents a number. When they are published they must satisfy `TravelMin < TravelMax` and `MaxVelocity > 0`; any other
combination (a partial publication included) makes the driver refuse to attach.

## Command semantics

- **Handshake.** Before the command write the driver writes the parameters it needs (`TargetPosition`, `Velocity`,
  `Acceleration`, registers 2–7) in one FC16, then `Command` and `CommandSeq` (registers 0–1) in a second FC16 — so
  the PLC never sees an edge before its parameters. The PLC accepts a command word whose `CommandSeq` differs from
  `CommandAck` and copies `CommandSeq` into `CommandAck` **in the same scan in which it enters the resulting state**
  (Homing, DiscreteMotion, ContinuousMotion, Stopping, Disabled, Standstill). A status read that shows the new ack
  therefore also shows the command's state, never the state from before it.
- **Acknowledge**: a command is accepted when `CommandAck == CommandSeq`. The driver waits ≤ 500 ms for the ack; no
  ack → SDK `CommunicationLost` for the command (the heartbeat keeps running); the driver clears the edge bit it set.
- **Sequence at attach**: the driver reads `CommandAck` and continues from `CommandAck + 1`, so a new driver never
  issues a sequence number the PLC already acknowledged.
- **Enable** (level): `Command.bit0 = 1` → PLC energises the drive; State leaves Disabled for Standstill.
  `bit0 = 0` → drive off, State = Disabled. After a watchdog trip or any entry into ErrorStop the PLC ignores bit 0
  until a Reset, and after the Reset it energises only on a fresh 0→1 of bit 0 — an axis never re-energises by
  itself. A move with Enable low is ignored and answered by `FaultCode = 0`, `State = Disabled`; the driver rejects
  it before writing (SDK `MotionError.Busy`).
- **Home** (edge): PLC runs its own homing sequence; State = Homing until done, then Standstill with `Homed`. The
  driver treats Home as callable from Disabled (it is the energise act: the driver sets Enable first). Failure →
  ErrorStop, `FaultCode = 5`.
- **MoveAbsolute** (edge): requires `Homed`; PLC moves to `TargetPosition` at `Velocity`; State = DiscreteMotion,
  then Standstill with `InPosition`. Target outside travel → the driver refuses (SDK `OutOfRange`) and writes nothing.
- **MoveVelocity** (edge): continuous motion at signed `Velocity` until Stop or a limit; State = ContinuousMotion.
  While `Homed`, reaching `TravelMin`/`TravelMax` is a controlled stop to Standstill (not a fault). A limit switch is
  always a fault: ErrorStop, `FaultCode = 2`.
- **Stop** (edge, priority): accepted in every state, Homing included; decelerate and hold; State = Stopping →
  Standstill. From Disabled or Standstill it is acknowledged and changes nothing. The driver sends Stop on the
  priority lane (see FR-11) and expects motion to cease within 200 ms of the write.
- **Reset** (edge): clears `FaultCode` and leaves ErrorStop → Disabled (Enable must be re-asserted, see Enable).
  Does not clear `WatchdogFault` (register 10 is cleared explicitly) and does not touch `Homed`. From any state
  other than ErrorStop it is acknowledged and changes nothing.
- **Limit switch recovery**: after a Reset with a limit switch still active, the PLC accepts motion only in the
  direction away from that switch.

## FR-11 — dead-commander watchdog and advisory lease (verbatim from epic-065)

Motion must cease when the commander **dies**, not only when it calls Stop. Modbus TCP has no liveness semantics; a
dead commander is indistinguishable from an idle one. Therefore:

- The driver runs a **connection-lifetime heartbeat** per axis, started at connect, stopped deliberately at
  disconnect, independent of motion, writing `Heartbeat` at **≥ 5 Hz**. Heartbeat traffic shares the channel with
  move traffic; heartbeat deferral is **bounded at 200 ms** so a long move cannot starve the beat.
- The PLC **arms** the watchdog on the **first change** of `Heartbeat` it observes while `WatchdogFault = 0` and
  `LeaseOwner ≠ 0` (so an idle PLC is not faulted one second after boot), then **trips** when `Heartbeat` has not
  changed for **1 s**: Enable dropped, velocity zeroed, State = ErrorStop, `FaultCode = 4`, `WatchdogFault = 1`,
  `WatchdogTrips += 1`. A trip does **not** touch `Homed` (recovery = Reset + re-command, no re-home) and
  re-asserts the limit functions. After a trip the network disarms until `WatchdogFault` has been written 0 and a new
  beat arrives.
- **Clean release disarms.** When `LeaseOwner` is written 0 the watchdog disarms without tripping. A clean
  disconnect is: Stop → Enable 0 → stop beating → `LeaseOwner = 0`. `WatchdogTrips` therefore counts dead
  commanders only. A killed process cannot write 0, so the kill case is untouched.
- **Advisory lease**: before attaching, the driver reads `LeaseOwner`. 0 → take it (write own id). Own id → reattach.
  Foreign id → watch `Heartbeat` at the heartbeat interval; unchanged for one expiry window (1 s) → the lease has
  expired, take it; still changing → refuse (SDK `LeaseHeld`), keep watching until the configured lease timeout.
  Watching continuously rather than in discrete 1 s samples lets a successor attach ≤ 1.1 s after the incumbent's
  last beat. "Advisory" is stated plainly: Modbus has no compare-and-swap, so two drivers starting in the same window
  can both pass; the watchdog bounds the consequence. On a clean disconnect the driver writes `LeaseOwner = 0` if it
  still holds it.
- **At attach** the driver writes `WatchdogFault = 0` after winning the lease: a latched trip then belongs to a dead
  predecessor, and leaving it latched would leave the network disarmed for the new commander. `FaultCode = 4` and
  ErrorStop stay until a Reset, so the trip remains visible and distinguishable from a drive fault.
- Owner id is a station-unique non-zero 16-bit value from host configuration.
- Each heartbeat tick is: write `Heartbeat`, read registers 10–11, read the status block. That tick is the status
  cadence of the axis, moving or idle.
- Stop and cancellation take a **priority lane** on the channel that preempts queued move traffic (≤ 200 ms to a
  halted axis).

## What the PLC program must do (checklist for the ladder / structured-text author)

1. Serve holding registers C+0…C+11 and S+0…S+14 on the configured unit id and port; refuse nothing, ignore writes
   to PLC-owned registers (11, 100–114).
2. Publish `MapVersion = 1`, `TravelMin/Max`, `MaxVelocity` from the axis parameters at start-up.
3. Mirror the drive: `State`, `Flags`, `ActualPosition`, `ActualVelocity`, `FaultCode` every PLC cycle; serve a
   status-block read from one scan's image.
4. Execute the command semantics above; echo `CommandSeq` into `CommandAck` in the scan that enters the command's
   state.
5. Implement the watchdog exactly as FR-11 states (arm on first change with a lease held, 1 s stall, trip actions,
   disarm until cleared, disarm on `LeaseOwner = 0`).
6. Keep the project in a git repository next to this file's copy (epic-065 risk R-1: an unversioned PLC program is a
   drive that answers every register and moves nothing after replacement).

## Scope of map version 1

- One axis per block pair. A PLC serving two axes places a second block pair at other bases and is configured as
  two devices.
- A rotary axis has limited travel: `ActualPosition` is absolute and never wraps. Endless rotation is not in
  version 1.

## Reference: the Delta positioner ladder this generalises

```
M0 -> M1025 (RUN) + M1040 (servo on)
M4 -> edges: MOV 0/1 D1060 (speed/position mode); FREQ D110 D111 D112
M5 -> M1026 (direction: OFF = forward, ON = reverse)
M6 + edge X7 -> DSUB D1051 D120 D122; DMOV D1051 D120   (home latch)
D130 heartbeat · D131 lease owner · D132 latched watchdog fault · D133 trip counter
```
(`delta-positioner/src/RocketWelder.SDK.Devices.Motion.Delta/DeltaRegisters.cs`, epic-065 `current-state.md`.)
