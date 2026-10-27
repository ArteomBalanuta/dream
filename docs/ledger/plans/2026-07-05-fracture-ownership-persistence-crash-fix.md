# Fracture Ownership Persistence Crash Fix

Purpose: Define the execution slice for the server-authoritative crash where fatal cube fracture persists child pieces with invalid ownership and kills the integrated server loop.

Status: In progress.

Scope: `WorldObjectRegistry` cube-record rebinding, `WorldSessionRuntime` fracture persistence flow, SQLite persistence invariants, and focused regression coverage.

## Problem Summary

The live diagnostics run from `logs/manual-play-20260705-194309/` showed that combat could appear to "stop working" because the authoritative server loop died after a fatal destructible-cube hit.

The crash occurred in:

- `WorldSessionRuntime.ProcessCombatUseAction`
- `WorldSessionRuntime.PersistFracturePieces`
- `SqliteWorldPersistenceRepository.SaveCubeFracturePiece`

The SQLite error was a foreign-key failure while inserting `cube_fracture_pieces`.

## Root-Cause Hypothesis

The current moved-cube fallback path can resolve a visible destructible cube through `WorldObjectRegistry.TryResolveCubeRecord` even when registry identity has drifted.

When there is exactly one matching cell-owned record, the code correctly rebinds the record and preserves:

- actor id
- health
- owner player name

But when the fallback cannot find a unique matching record, it currently creates a brand-new cube record with:

- default health
- new actor id
- no owner player name

That makes the cube combat-resolvable, but not persistence-safe. If that ownerless cube is destroyed and fractured, `WorldSessionRuntime` falls back to `"unknown-player"` and writes fracture pieces whose `owner_player_name` does not exist in `player_profiles`.

That violates the `cube_fracture_pieces.owner_player_name -> player_profiles.player_name` foreign key and terminates the authoritative server loop.

## Fix Goals

1. Preserve authoritative destructible ownership across visible-cube fallback resolution.
2. Prevent ownerless destructible full cubes from reaching fracture persistence.
3. Fail safely if persistence still encounters an invalid ownership state, instead of killing the server loop.
4. Add regression tests that prove moved/rebound cubes remain damageable and persistence-safe.

## Implementation Tasks

### 1. Harden cube-record fallback ownership

- Update `WorldObjectRegistry.TryResolveCubeRecord` so the non-exact fallback prefers authoritative state over creating a fresh anonymous record.
- Preserve existing `OwnerPlayerName`, `Actor.Id`, and health whenever a visible cube can be matched to a prior destructible cube lineage.
- If the fallback truly cannot prove authoritative ownership, avoid creating a persistence-breaking anonymous destructible record.

### 2. Guard fracture persistence invariants

- Update `WorldSessionRuntime.PersistFracturePieces` so persisted fracture groups only use validated owner names.
- If a cube record lacks a valid persistence owner, do not emit `"unknown-player"` into DB-backed fracture persistence.
- Keep runtime fracture behavior deterministic and survivable even if persistence must skip or log the invalid write.

### 3. Add regression coverage

- Extend combat or registry tests so visible moved cubes keep owner identity after fallback resolution.
- Add an authoritative runtime test for fatal cube fracture after identity drift:
  - resolved cube remains damageable
  - fracture persistence uses a valid owner name
  - no repository exception escapes the runtime path

### 4. Verify

- Focused tests for combat resolver/runtime fracture behavior
- Full `dream.Tests`
- Full `dream` build

## Expected Outcome

After the fix:

- destroying a moved/rebound placed cube must not kill the authoritative server loop
- fracture pieces must persist under a valid player owner
- later combat actions must continue to be processed because server ticks continue running
