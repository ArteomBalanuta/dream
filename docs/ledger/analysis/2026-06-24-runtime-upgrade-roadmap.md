# Runtime Upgrade Roadmap Analysis

**Purpose:** Assess the current .NET/C# and package baseline, identify which upgrades are low-risk, and separate them from the physics-library modernization work.

**Status:** Complete

**Scope:** Target framework, C# language version, runtime dependencies, test dependencies, and physics-library direction

## Current Baseline

From the current project files and local SDK:

- `dream/dream.csproj` targets `net8.0`
- no explicit `LangVersion` is set, so the language version follows the target framework defaults
- local SDK is `10.0.201`
- the current top-level runtime packages are:
  - `BulletSharp` `0.11.1`
  - `ImGui.NET` `1.91.6.1`
  - `OpenTK` `4.9.3`
  - `OpenTK.Windowing.Desktop` `4.9.3`
  - `SharpGLTF.Core` `1.0.3`
  - `SixLabors.ImageSharp` `3.1.6`
  - `System.Drawing.Common` `9.0.1`

The current test packages are:

- `Microsoft.NET.Test.Sdk` `17.11.1`
- `xunit` `2.9.2`
- `xunit.runner.visualstudio` `2.8.2`

## External Reality Check

Based on current official package and Microsoft documentation sources checked on June 24, 2026:

- `OpenTK` has a newer stable `4.9.4`
- `SharpGLTF.Core` has a newer stable `1.0.6`
- `SixLabors.ImageSharp` has a newer stable `4.0.0`
- `System.Drawing.Common` has a newer package, but Microsoft continues to document it as Windows-only in modern .NET
- `BulletSharp` on NuGet is still `0.11.1`, and the package still restores as .NET Framework-oriented, which is the source of the current `NU1701` compatibility warning
- the original BulletSharp ecosystem appears effectively stale compared with the rest of the stack
- modern C# guidance from Microsoft ties stable `C# 14` to `.NET 10`; `C# 15` is preview-only and tied to `.NET 11` preview

## Key Findings

### 1. The project is already running on an old TFM relative to the installed SDK

Because the machine already has `.NET SDK 10.0.201`, the project can reasonably target `.NET 10` after dependency review. That would move the codebase onto the current stable runtime and unlock stable `C# 14`.

### 2. The real blocker is not C# itself; it is BulletSharp

The package drift warnings are manageable for most dependencies, but `BulletSharp` is structurally different:

- it is old
- it restores as .NET Framework-compatible rather than modern `.NET`
- it is already producing `NU1701`
- it is tightly coupled into gameplay code

This means the correct upgrade plan is not "find a newer BulletSharp version" because there does not appear to be a healthy direct successor in the same mainline package path.

### 3. Some dependency upgrades are immediate wins

The following can be treated as low-risk or medium-risk incremental upgrades:

- `OpenTK` and `OpenTK.Windowing.Desktop`
- `SharpGLTF.Core`
- `SixLabors.ImageSharp`
- test packages

These should be handled before the physics migration so the risky work is isolated.

### 4. `System.Drawing.Common` looks removable

A repo search found the package reference in the project file, but no actual `System.Drawing` usage in the game code. That makes this package a likely cleanup candidate rather than an upgrade candidate.

### 5. Best-practice physics direction is replacement, not preservation

For modern C# development, the cleanest long-term direction is to migrate away from `BulletSharp` to a maintained physics stack rather than carry a stale compatibility layer forward.

The best-practice recommendation for this repo is:

- short term: isolate the current physics API behind the existing facade boundary
- medium term: replace BulletSharp with a maintained engine

Among likely options, a pure-C# engine such as `BepuPhysics v2` is the strongest modernization fit because it avoids native-wrapper drift and aligns better with modern .NET toolchains.

## Recommended Upgrade Strategy

Use a two-track roadmap:

### Track A: Safe modernization

- move to `.NET 10`
- let the project use stable `C# 14`
- centralize package versions
- upgrade routine dependencies
- remove dead dependencies such as `System.Drawing.Common` if verification confirms they are unused

### Track B: Physics modernization

- create a tighter physics boundary first
- preserve current gameplay behavior under tests
- replace `BulletSharp` behind that boundary
- only after the replacement succeeds, remove all Bullet-specific helpers and conversions

## Recommendation

Do **not** jump directly from the current state to a "full everything upgrade in one commit."

The safest and most maintainable path is:

1. package-management cleanup
2. safe dependency upgrades
3. target framework upgrade to `.NET 10`
4. physics abstraction hardening
5. BulletSharp replacement

That sequencing keeps the unavoidable risk concentrated in one well-defined migration phase instead of spreading it across the whole repository.
