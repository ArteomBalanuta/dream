# 2026-07-05 Moved Cube Combat Registry Rebind

## Summary

- Fixed a combat resolution drift where a visible moved full cube could stop taking damage if its `WorldObjectRegistry` entry no longer matched the exact in-map `Cube` instance.
- Added a registry repair path that resolves cube combat targets by authoritative occupied cell and rebinds the world-object record to the visible cube when identity drift is detected.
- Added regression coverage for both the low-level combat resolver and the authoritative runtime combat path.

## Implementation Notes

- `CombatUseResolver.TryResolveDestructibleCubeHit(...)` now asks the registry to resolve or heal destructible cube ownership instead of relying on exact object-reference lookup only.
- `WorldObjectRegistry.TryResolveCubeRecord(...)` now:
  - returns exact matches when present,
  - falls back to authoritative occupied-cell resolution for visible full cubes,
  - rebinds an orphaned/desynced record onto the visible cube when a single matching cube record exists at that cell,
  - and, as a last guard, re-registers a visible non-floor cube so combat does not silently miss a valid destructible target.

## Verification

- Added resolver regression for a moved full cube whose registry identity drifted.
- Added authoritative runtime regression proving a moved placed cube remains damageable and still fractures after its registry link is removed.
