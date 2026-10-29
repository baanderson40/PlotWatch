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
