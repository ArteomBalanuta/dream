Purpose: Record the wolf animation and facing behavior fix.
Status: Completed
Scope: Enemy movement facing, enemy behavior replication, and remote enemy animation continuity.

## Summary

The wolf visual behavior had two linked problems:

- enemy facing was updated toward the player before movement resolution, so the wolf could visually track the player even when idle or when collision redirected movement
- remote enemy snapshots recreated `EnemyActor` instances every update, which reset `EnemyVisualState` and could restart walk/attack clips before they visibly advanced

## Completed Work

- Changed `EnemyActor.Update(...)` so idle wolves outside aggro range keep their current facing.
- Changed chase facing to follow the actual resolved movement delta instead of the raw player direction.
- Kept melee attack facing pointed at the target so bite presentation still aims at the victim.
- Extended `ActorSnapshot` with optional `EnemyBehaviorState` for replicated enemy visual behavior.
- Exported enemy behavior state from authoritative snapshots.
- Changed `RemoteEnemyStateApplier` to upsert existing enemies and preserve their `EnemyVisualState`.
- Added focused tests for natural facing, remote upsert behavior, and protocol round-trip of enemy behavior state.
- Updated rendering docs to note that remote enemy actor identity must be preserved for animation continuity.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~EnemyActorTests|FullyQualifiedName~RemoteEnemyStateApplierTests|FullyQualifiedName~ProtocolFoundationTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`

The full suite passed with `526` tests. The build passed with the existing `SQLitePCLRaw.lib.e_sqlite3` vulnerability warning and `BulletSharp` compatibility warning.
