Purpose: Record the runtime asset-normalization seam added after the wolf model rendered at an oversized scale.
Status: Complete
Scope: World-asset size metadata, wolf normalization migration, and verification

# Summary

This change replaced the wolf's opaque render-scale constant with explicit world-asset normalization metadata.

Completed work:

- added ledger analysis and an implementation plan for world-asset normalization
- introduced `WorldAssetNormalization`
- migrated `WolfAssetDefinition` to declare:
  - `AuthoredHeight = 3.191f`
  - `TargetWorldHeight = 1.20f`
- changed `EnemyRenderState.CreateWolf(...)` to derive its default scale from normalization metadata
- added focused regression tests for the pure normalization type, wolf metadata, and wolf render-state scale

# Why

The wolf asset is authored at about `3.191` world units tall once its own node-local scale is applied. That is too large for the current player/world proportions.

The runtime now treats that as an explicit normalization concern instead of burying the answer in a one-off visual scale constant.

# Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~WorldAssetNormalizationTests|FullyQualifiedName~WolfAssetDefinitionTests|FullyQualifiedName~EnemyRenderStateTests" --nologo /p:UseSharedCompilation=false
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Observed results:

- focused normalization tests passed
- full test suite passed
- build passed with the known BulletSharp warning still present
