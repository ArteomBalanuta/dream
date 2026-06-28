# Floating Combat Text Plan

Date: 2026-06-28
Status: Planning
Owner: Codex

## Goal

Implement MU-inspired floating damage text in a way that fits the current HUD and render architecture, using the visual timing and behavior defined in [DMG_IMPL.md](G:\workspace\projects\dream\DMG_IMPL.md).

## Why This Comes First

- strongest existing spec
- clear visual payoff
- fits the current renderer layering
- lower risk than starting with a full combat actor hierarchy

## Repo-Fit Architecture

Recommended new units:

- `FloatingCombatTextEntry`
  - runtime state for a single active text
- `FloatingCombatTextStyle`
  - color, scale modifier, layer priority
- `FloatingCombatTextPool`
  - bounded reuse of transient entries
- `FloatingCombatTextSystem`
  - spawn, update, expire, merge, and cap logic
- `FloatingCombatTextRenderer`
  - HUD-side projection and draw submission

Likely integration points:

- `HudRenderer`
- `Render`
- a world-to-screen helper driven by current camera and viewport

## Scope

### Phase 1 Scope

- regular damage text
- incoming-damage text
- burst / float / fade lifecycle
- screen-space rendering from world anchors
- pooling
- active-per-target cap

### Deferred

- complete MU style matrix
- text merging
- critical/excellent/ignore-defense variants
- DoT variant styling

## Delivery Steps

1. Add analysis-backed spec for the floating-combat-text system if a deeper spec is needed.
2. Add a world-anchor to screen-position conversion path in the renderer/HUD layer.
3. Implement a small pooled transient-text runtime system.
4. Implement the three-phase lifecycle from `DMG_IMPL.md`.
5. Wire one controlled spawn path for manual/runtime testing.
6. Add tests for lifecycle math and cap behavior.
7. Add docs and work-log entry.

## Success Criteria

- text appears above a target anchor in screen space
- spawn includes burst scale-up and slight horizontal offset
- upward motion follows ease-out behavior
- alpha fades to zero before return to pool
- bounded active count prevents unreadable spam

## Dependencies

- active camera access
- viewport metrics
- a stable target anchor source for the spawn origin

## Notes

This plan should use the existing `DMG_IMPL.md` as the visual truth source instead of re-inventing the feel from scratch.
