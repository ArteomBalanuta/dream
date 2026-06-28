Purpose: Record the wolf movement-facing correction.
Status: Completed
Scope: Enemy visual transform metadata and regression coverage.

# Wolf Forward Orientation

## Summary

The wolf render transform now maps the asset's authored forward axis to the gameplay movement direction. The imported wolf asset is treated as facing local `+Z`, so `EnemyRenderState.CreateWolf` resolves yaw with `atan2(forward.X, forward.Z)` instead of the previous engine-side `-Z` assumption.

## Why

The wolf could move toward the player while visually facing backward. A constant 180-degree flip would fix forward/backward motion but mirror lateral movement, so the correction was made as an asset-axis mapping instead.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter EnemyRenderStateTests /p:UseSharedCompilation=false`
