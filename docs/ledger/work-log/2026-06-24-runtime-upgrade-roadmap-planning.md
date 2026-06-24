# Runtime Upgrade Roadmap Planning Work Log

## Summary

Reviewed the current project baseline and produced a staged modernization plan for the runtime stack.

## Findings

- the repo currently targets `net8.0`
- the local installed SDK is `10.0.201`
- `BulletSharp` is the main modernization blocker because it is stale and restores as a .NET Framework-oriented package
- `OpenTK`, `SharpGLTF.Core`, `SixLabors.ImageSharp`, and the test packages have straightforward update paths
- `System.Drawing.Common` appears unused and is a likely removal candidate rather than an upgrade candidate

## Outputs

- analysis:
  - `docs/ledger/analysis/2026-06-24-runtime-upgrade-roadmap.md`
- plan:
  - `docs/ledger/plans/2026-06-24-runtime-upgrade-roadmap.md`

## Recommendation

Use a two-track roadmap:

1. safe dependency and framework modernization
2. isolated physics-library replacement

That keeps the unavoidable risk concentrated in the physics migration instead of mixing it with every other upgrade.
