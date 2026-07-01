Purpose: record the player-model normalization pass that shrank the blocky player to the current world-unit target
Status: completed
Scope: player asset bounds measurement, metadata-derived scale, focused regression coverage, and docs alignment

# Player Model Size Normalization

## Summary

The blocky player model no longer uses a raw `VisualScale = 0.85f` constant.

This pass added:

- `WorldAssetDimensions` as a cube-relative size contract
- `WorldAssetBounds.MeasureDimensions(...)` so imported glTF actor dimensions can be measured from authored mesh/node bounds
- a metadata-derived `PlayerModelAssetDefinition.Normalization`
- a compact player target of `1.2` world units tall, currently `2x` the wolf target height, while keeping width/depth capped to one cube or less in neutral stance

The grounding offset stayed on the existing body-center-to-foot seam so the size reduction does not reintroduce the floating-player regression.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~WorldAssetBoundsTests|FullyQualifiedName~PlayerModelAssetDefinitionTests|FullyQualifiedName~WorldAssetNormalizationTests|FullyQualifiedName~PlayerRenderStateTests"`
