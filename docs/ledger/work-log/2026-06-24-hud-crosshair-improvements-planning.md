# HUD Crosshair Improvements Planning Work Log

**Purpose:** Record the analysis and planning pass for the HUD crosshair follow-up in `dream`.

**Status:** Complete

**Scope:** Root-cause analysis, design direction, and implementation planning for the off-center crosshair and HUD cleanup

## Summary

Analyzed the current HUD pipeline after the user reported that the crosshair was not centered. Confirmed that the crosshair vertices were still hardcoded for an `800 x 600` layout even though the renderer now projects against the live viewport size. Chose a viewport-driven HUD layout approach so the fix addresses both the immediate bug and the structure that allowed it.

## Outputs Created

- `docs/ledger/analysis/2026-06-24-hud-crosshair-improvements.md`
- `docs/ledger/specs/2026-06-24-hud-crosshair-improvements.md`
- `docs/ledger/plans/2026-06-24-hud-crosshair-improvements.md`

## Approved Direction

- keep the crosshair as a raw OpenGL HUD primitive
- compute layout from live viewport size
- allow the crosshair to scale with resolution
- separate HUD layout math from raw renderer submission
- add unit tests for crosshair geometry math

## Planned Verification

The implementation plan requires:

- focused HUD layout unit tests
- full project build verification
- runtime visual confirmation at multiple resolutions
