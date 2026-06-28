# Creature Support Foundation

Purpose: record the first implementation slice of the shared combat-capability layer.
Status: completed
Scope: player combat foundation, shared contracts, and HUD anchor integration

## Summary

Implemented a minimal combat support layer that future enemies and destructible objects can share without introducing a deep `Creature` inheritance tree.

## Completed Work

- added `IDamageable` and `ICombatAnchorProvider` as the first shared combat contracts
- added `CreatureKind`, `CombatActorHandle`, `DamageReceipt`, and `HealthState` as reusable combat primitives
- added `PlayerCombatant` as a player-owned combat capability component backed by shared health state
- integrated `PlayerCombatant` into `Player` while keeping movement, camera, and physics responsibilities separate
- updated HUD combat-text spawning so it can anchor from a `CombatActorHandle` instead of only from ad hoc world positions
- added focused tests for combat health, actor anchors, and player combatant behavior

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo --filter "FullyQualifiedName~HealthStateTests|FullyQualifiedName~CombatActorHandleTests|FullyQualifiedName~PlayerCombatantTests"`

## Related Records

- plan: `docs/ledger/plans/2026-06-28-creature-support-foundation.md`
- analysis: `docs/ledger/analysis/2026-06-28-combat-proposal-refactor.md`
