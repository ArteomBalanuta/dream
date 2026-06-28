Purpose: Record the wolf collision/separation pass that replaced render-only spacing.
Status: Completed
Scope: Wolf close-range placement, enemy movement blocking, player/enemy overlap handling, and regression coverage.

# Wolf Collision Separation

## Summary

The earlier render-side spacing workaround made the wolf model jump away from the camera when the player got close. This pass removes that render-only displacement path and keeps the wolf rendered at its real actor position.

Enemy movement now resolves chase steps against map occupancy, so wolves do not move through placed cubes. The scene also applies a lightweight player/enemy separation pass when noclip is off, keeping the player outside the wolf footprint without changing flying behavior.

## Notes

- `WolfAssetDefinition` now declares collision footprint metadata alongside size and facing metadata.
- Enemy debug collision geometry uses the actor collision dimensions.
- The current approach is still a first-pass gameplay collision layer, not a full Bullet enemy body.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "EnemyMovementCollisionTests|EnemyPlayerCollisionResolverTests|EnemyActorTests|EnemyRegistryTests|WolfAssetDefinitionTests" /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
