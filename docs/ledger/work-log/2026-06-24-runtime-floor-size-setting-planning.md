# Runtime Floor Size Setting Planning Work Log

**Purpose:** Record the analysis and planning pass for runtime floor resizing in `dream`.

**Status:** Complete

**Scope:** Root-cause analysis, design direction, and implementation planning for runtime floor-size changes

## Summary

Analyzed the current world, settings, and physics structure and concluded that runtime floor resizing should be implemented as a controlled world rebuild with migration rather than an in-place mutation of the current map. The chosen direction preserves the player and any in-bounds placed cubes, recreates the merged floor collider for the new size, and applies the change only through an explicit settings action.

## Outputs Created

- `docs/ledger/analysis/2026-06-24-runtime-floor-size-setting.md`
- `docs/ledger/specs/2026-06-24-runtime-floor-size-setting.md`
- `docs/ledger/plans/2026-06-24-runtime-floor-size-setting.md`

## Approved Direction

- expose a square floor-size runtime setting
- apply it through an explicit button
- rebuild the map for the new size
- migrate surviving non-floor cubes
- preserve or clamp the player position safely

## Planned Verification

The implementation pass should verify:

- map construction/migration tests
- full test suite success
- build success
- successful runtime startup
