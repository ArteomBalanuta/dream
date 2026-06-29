Purpose: Record the fix for the invisible wolf model and the first-pass asset-support work needed to render the current skinned creature asset.
Status: Complete
Scope: Animated-model transform composition, first-pass glTF skinning support, and related documentation/tests

# Summary

This work fixed the current wolf-visibility bug and closed the most important missing asset-support gap for the creature milestone.

Completed changes:

- added an investigation ledger and implementation plan for the wolf asset rendering gap
- introduced `AnimatedModelTransformComposer` and switched node/world transform composition to the correct order
- added `GltfSkinningAnalysis` plus focused tests proving the wolf asset is a skinned glTF mesh
- extended `cubeVertex.glsl` with a bounded first-pass skinning contract
- extended `AnimatedModel` to detect skinning data, upload a joint palette, and apply skinning for primitives that use joint weights
- updated project docs to describe the current first-pass skinned-model support level

# Root Cause

The immediate invisibility bug came from incorrect matrix composition in `AnimatedModel.RenderPrepared()`. The wolf mesh node has an authored local `100x` scale, and the old composition multiplied the actor translation by that node-local scale, pushing the rendered mesh far away from the debug/combat position.

The broader asset-support gap was that `AnimatedModel` already loaded `JOINTS_0` and `WEIGHTS_0`, but the active shader path never used them. That meant the wolf asset was only partially supported even after the visibility fix.

# Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~AnimatedModelTransformComposerTests|FullyQualifiedName~GltfSkinningAnalysisTests|FullyQualifiedName~CubeVertexShaderSkinningContractTests" --nologo /p:UseSharedCompilation=false
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
dotnet run --project dream\dream.csproj --nologo
```

Observed results:

- targeted tests passed
- full test suite passed
- build passed with the known BulletSharp warning still present
- app startup completed without the previous wolf-rendering crash path; runtime startup was exercised to force shader compilation
