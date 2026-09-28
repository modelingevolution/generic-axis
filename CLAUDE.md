# generic-axis

Generic external axis (linear track / rotary positioner) for RocketWelder over Modbus TCP. The protocol is
`docs/protocol.md`; the driver, the rw2 plugin, the tests and the single test app live under `src/`.

## Build and test

```bash
dotnet build src/GenericAxis.sln
dotnet test  src/GenericAxis.sln
```

Native Linux `dotnet`, never `dotnet.exe`. `src/NuGet.config` clears the machine-level sources on purpose (the org
feed is unreachable from Windows and from GitHub runners; everything needed is on nuget.org).

## Rules

- **The protocol file is the contract.** A register, a bit or a timing rule that is not in `docs/protocol.md` does
  not exist. Change the document first, in the same PR as the code.
- **PackageReference only** across repos. The plugin is loaded standalone by rw2's `PluginLoader`.
- **No version numbers in csprojs.** `src/Directory.Build.props` holds `1.0.0` placeholders the release workflow
  rewrites from the git tag.
- **Never publish from a workstation.** Push a `vX.Y.Z` tag; `.github/workflows/release.yml` packs and publishes.
- **Limits come from the machine** (`TravelMin/Max`, `MaxVelocity` registers) — the driver refuses, it never clamps
  and never invents a number.
- **A dry run is a station run**: nothing here knows about welding; an axis moves the same way in a dry run.
- Mirror `delta-positioner` (driver shape, heartbeat, lease, priority gate) and `delta-positioner-sim` (simulator
  shape) — do not import their code as a dependency; neither publishes a package.
