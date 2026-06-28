Purpose: Record the skinning-space correction for the wolf mesh deformation issue.
Status: Complete
Scope: glTF joint palette composition for skinned world models

# Summary

The wolf was visible but its triangles stretched dramatically because the first-pass skinning palette was composed in the wrong transform space.

Investigation showed that the old bind-pose transform:

```csharp
jointWorldMatrix * inverseBindMatrix
```

could move sample bind-pose vertices by several world units. The corrected transform accounts for the rendered mesh node's world transform:

```csharp
inverseBindMatrix * jointWorldMatrix * inverseMeshWorldMatrix
```

This keeps bind-pose vertices near their mesh-local positions before the normal model matrix is applied.

# Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~AnimatedModelTransformComposerTests|FullyQualifiedName~CubeVertexShaderSkinningContractTests|FullyQualifiedName~GltfSkinningAnalysisTests" --nologo /p:UseSharedCompilation=false
dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false
dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false
```

Observed results:

- focused skinning-related tests passed
- full test suite passed
- build passed with the known BulletSharp warning still present
