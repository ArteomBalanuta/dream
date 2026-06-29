Purpose: Record the wolf visual-size reduction requested after runtime tuning.
Status: Completed
Scope: Wolf world-asset normalization and matching first-pass collision footprint metadata.

# Wolf Half Height

## Summary

The wolf target world height was reduced from `1.20` world units to `0.60` world units through `WolfAssetDefinition.Normalization`. This keeps the change in the existing imported-asset normalization seam instead of adding a separate render scale override.

The first-pass wolf collision footprint was reduced with the visual target height:

- radius: `0.45` -> `0.30`
- height: `1.20` -> `0.60`

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "WolfAssetDefinitionTests|EnemyRenderStateTests|EnemyMovementCollisionTests|EnemyPlayerCollisionResolverTests" /p:UseSharedCompilation=false`
