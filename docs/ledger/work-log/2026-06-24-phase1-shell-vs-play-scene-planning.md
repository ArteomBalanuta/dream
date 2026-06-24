# Phase 1 Shell Vs Play Scene Planning Log

## Summary

Prepared the first concrete phase from the game-design pattern roadmap:

- split `Game` into a window shell
- introduce `PlayScene` as the active gameplay owner

## Outputs

- analysis:
  - `docs/ledger/analysis/2026-06-24-phase1-shell-vs-play-scene.md`
- spec:
  - `docs/ledger/specs/2026-06-24-phase1-shell-vs-play-scene.md`
- implementation plan:
  - `docs/ledger/plans/2026-06-24-phase1-shell-vs-play-scene.md`

## Key Intent

This phase is designed to:

- reduce pressure on `dream/Game.cs`
- create a clean seam for later roadmap phases
- preserve all current gameplay behavior while improving structure
