# Multiplayer Roadmap Planning

Purpose: record the multiplayer and dedicated-server roadmap design pass for `dream`.
Status: completed
Scope: planning-only work for authoritative server architecture, client/server refactor seams, and the first multiplayer implementation roadmap

## Summary

Produced a repo-specific multiplayer roadmap focused on:

- one shared world session per dedicated server process
- many clients connected to the same authoritative simulation
- a 64 Hz server tick
- practical client-local presentation boundaries
- a phased path from the current `PlayScene` architecture to a working multiplayer PoC

## Outputs

- analysis:
  - `docs/ledger/analysis/2026-06-29-client-server-refactor-seams.md`
  - `docs/ledger/analysis/2026-06-29-multiplayer-authoritative-server-roadmap.md`
  - `docs/ledger/analysis/2026-06-29-player-session-and-command-model.md`
  - `docs/ledger/analysis/2026-06-29-replication-and-presentation-boundaries.md`
  - `docs/ledger/analysis/2026-06-29-fastest-working-transport-poc.md`
  - `docs/ledger/analysis/2026-06-29-authoritative-build-combat-validation-flow.md`
  - `docs/ledger/analysis/2026-06-29-client-server-protocol-shape.md`
  - `docs/ledger/analysis/2026-06-29-multiplayer-foundation-prerequisites.md`
- spec:
  - `docs/ledger/specs/2026-06-29-dedicated-server-session-architecture.md`
  - `docs/ledger/specs/2026-06-29-multiplayer-command-and-replication-model.md`
  - `docs/ledger/specs/2026-06-29-remote-player-presentation-and-interpolation.md`
  - `docs/ledger/specs/2026-06-29-client-server-protocol-contract.md`
- plan:
  - `docs/ledger/plans/2026-06-29-multiplayer-foundation-roadmap.md`
  - `docs/ledger/plans/2026-06-29-phase0-multiplayer-preflight-and-runtime-audit.md`
  - `docs/ledger/plans/2026-06-29-phase1-world-session-runtime-extraction.md`
  - `docs/ledger/plans/2026-06-29-phase2-player-session-and-command-frames.md`
  - `docs/ledger/plans/2026-06-29-phase3-server-tick-and-snapshot-foundation.md`
  - `docs/ledger/plans/2026-06-29-phase4-protocol-contract-and-routing.md`
  - `docs/ledger/plans/2026-06-29-phase4-dedicated-server-host-and-transport-poc.md`
  - `docs/ledger/plans/2026-06-29-phase5-remote-player-presentation-and-interpolation.md`
  - `docs/ledger/plans/2026-06-29-phase6-authoritative-build-combat-validation-and-correction.md`
- decision:
  - `docs/ledger/decisions/2026-06-29-authoritative-server-tick-boundary.md`

## Key Direction

- extract a host-agnostic simulation runtime from the current client scene flow
- make player commands and player sessions explicit
- place world, combat, and physics truth on the server tick
- keep viewmodels, HUD, and similar presentation systems local to the client
- prioritize a working shared-world PoC before broader networking ambitions

## Verification

Verified file placement and naming against the current ledger structure.

No build or runtime verification was required because this planning pass only adds documentation.
