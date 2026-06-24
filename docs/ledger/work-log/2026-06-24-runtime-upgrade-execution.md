# Runtime Upgrade Execution Work Log

## Summary

Executed the safe modernization portion of the runtime upgrade roadmap while intentionally keeping `BulletSharp`.

## Implemented Changes

- added `global.json` pinned to SDK `10.0.201`
- added `Directory.Packages.props` for central package management
- upgraded the main project and test project from `net8.0` to `net10.0`
- upgraded routine dependencies:
  - `OpenTK` -> `4.9.4`
  - `OpenTK.Windowing.Desktop` -> `4.9.4`
  - `SharpGLTF.Core` -> `1.0.6`
  - `SixLabors.ImageSharp` -> `3.1.12`
  - `Microsoft.NET.Test.Sdk` -> `18.7.0`
  - `xunit` -> `2.9.3`
  - `xunit.runner.visualstudio` -> `3.1.5`
- removed the unused `System.Drawing.Common` package reference

## Important Adjustment

The original roadmap proposed moving `SixLabors.ImageSharp` to `4.0.0`. During execution, that upgrade failed because `4.0.0` now requires a build-time Six Labors license file or key for direct package dependencies.

To keep the repo buildable without introducing licensing configuration, the package was upgraded to `3.1.12` instead.

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
- launched `dream\\bin\\Debug\\net10.0\\dream.exe`, observed it stayed alive for 5 seconds, then stopped it cleanly

## Remaining Warnings

- `NU1701` remains for `BulletSharp 0.11.1`
- existing nullable and obsolete API source warnings remain

## Result

The project now builds and tests on `.NET 10` with centralized package versions, while preserving the current BulletSharp-based physics stack.
