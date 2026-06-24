# Performance First-Pass Planning Work Log

**Purpose:** Record the analysis and planning pass for the large-floor performance regression in `dream`.

**Status:** Complete

**Scope:** Root-cause analysis, design direction, and implementation planning for current-architecture performance improvements

## Summary

Analyzed the current floor-scaling slowdown and found that the project does much more work than the visible floor suggests. The main costs come from cubic eager world allocation, full-volume per-frame render iteration, one rigid body per floor tile, dynamic-style transform syncing for map bodies, and repeated shader uniform lookups in the draw loop.

## Outputs Created

- `docs/ledger/analysis/2026-06-24-performance-first-pass.md`
- `docs/ledger/specs/2026-06-24-performance-first-pass.md`
- `docs/ledger/plans/2026-06-24-performance-first-pass.md`

## Approved Direction

- keep the current architecture for this pass
- fix the obvious waste first
- separate floor dimensions from cubic world dimensions
- use sparse empty-space representation and direct solid-content lists
- replace per-tile static floor physics with one merged floor collider
- cache shader uniform locations

## Planned Verification

The implementation pass should verify:

- build success
- test success
- successful runtime startup
- that the structural hot spots are actually removed from the code path
