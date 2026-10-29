# PlotWatch

## Build

Run from `/mnt/d/Coding/C#/PlotWatch`.

Use the installed Windows .NET SDK from WSL:

```sh
"/mnt/c/Program Files/dotnet/dotnet.exe" build PlotWatch/PlotWatch.csproj -c Release
```

Requires .NET 10, Dalamud API 15 development files, and Windows/x64.

Build output: `PlotWatch/bin/Release/PlotWatch.dll`.

## Tests

Run the standalone policy/logic tests from this directory with the installed Windows .NET SDK:

```sh
"/mnt/c/Program Files/dotnet/dotnet.exe" run --project PlotWatch.Tests/PlotWatch.Tests.csproj
```

`PlotWatch.Tests` is a console test runner that links testable production source files directly. Add regression tests there as each testable feature is developed; test production logic rather than copying its implementation into the test project. Keep tests independent of Dalamud runtime where possible, using narrow stubs only for framework marker interfaces. UI, game reads, IPC, and automation still require in-game validation and must not be reported as covered by these offline tests.

## Architecture and schedule contracts

- `Lottery/LotterySchedule.cs` owns pure nine-day cycle math. Keep anchor and all schedule timestamps UTC; cycle is five application days followed by four results days. The current anchor is 2026-11-01 15:00 UTC, sourced from the linked Xenoveritas timer and user-provided schedule.
- Main-window schedule snapshots are refreshed on framework updates at a monotonic one-second cadence. Rendering consumes the cached snapshot; do not read the system clock in ImGui draw methods or decrement a stored countdown.
- Local display must use `TimeZoneInfo` conversion per timestamp and show numeric UTC offset. UTC/local choice lives in versioned configuration and must not reset existing settings on migration.
- Add boundary, cycle, timezone/DST, clock-jump, and migration tests to `PlotWatch.Tests` whenever schedule behavior changes.
