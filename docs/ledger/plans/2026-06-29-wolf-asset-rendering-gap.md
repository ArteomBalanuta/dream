# Wolf Asset Rendering Gap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the wolf enemy render at the correct world position and add first-pass glTF skinning support so its skinned mesh can animate through the existing `AnimatedModel` pipeline.

**Architecture:** Keep the fix bounded to the asset/rendering seam. Add pure helpers for transform composition and skinning analysis, then extend `AnimatedModel` and `cubeVertex.glsl` to upload and apply a first-pass joint palette without changing scene gameplay ownership.

**Tech Stack:** C# / .NET 10, OpenTK, SharpGLTF, GLSL, xUnit

---

### Task 1: Lock the transform root cause with a failing test

**Files:**
- Create: `dream/AnimatedModelTransformComposer.cs`
- Create: `dream.Tests/AnimatedModelTransformComposerTests.cs`

- [ ] **Step 1: Write the failing transform-composition test**

Add a pure test that proves an instance translation must not be multiplied by a node-local scale:

```csharp
[Fact]
public void ComposeInstanceNodeMatrix_Preserves_Instance_Translation_When_Node_Has_Local_Scale()
{
    Matrix4 instance = Matrix4.CreateScale(0.85f) * Matrix4.CreateTranslation(new Vector3(5.5f, 0f, 2.5f));
    Matrix4 nodeWorld = Matrix4.CreateScale(100f) * Matrix4.CreateTranslation(new Vector3(0f, 0f, 0.061579935f));

    Matrix4 combined = AnimatedModelTransformComposer.ComposeInstanceNodeMatrix(instance, nodeWorld);

    Assert.InRange(combined.M41, 5.49f, 5.51f);
    Assert.InRange(combined.M43, 2.54f, 2.57f);
}
```

- [ ] **Step 2: Run the focused test and confirm it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~AnimatedModelTransformComposerTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the helper does not exist yet.

- [ ] **Step 3: Add the transform helper and wire it into `AnimatedModel`**

Create a pure helper that composes node transforms in the correct order for the current renderer conventions, then replace the inline composition in `AnimatedModel.RenderPrepared()`.

- [ ] **Step 4: Re-run the focused transform test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~AnimatedModelTransformComposerTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 2: Lock the asset-support gap with skinning analysis tests

**Files:**
- Create: `dream/GltfSkinningAnalysis.cs`
- Create: `dream.Tests/GltfSkinningAnalysisTests.cs`

- [ ] **Step 1: Write the failing skinning-analysis tests**

Add tests that prove the repo wolf asset is a skinned mesh:

```csharp
[Fact]
public void Analyze_WolfAsset_Detects_Skinned_Primitives()
{
    GltfSkinningAnalysisResult result = GltfSkinningAnalysis.Analyze(WolfAssetDefinition.ModelPath);

    Assert.True(result.HasSkins);
    Assert.True(result.HasSkinnedPrimitives);
    Assert.Equal(49, result.JointCount);
}
```

- [ ] **Step 2: Run the focused test and confirm it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GltfSkinningAnalysisTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the analysis type does not exist yet.

- [ ] **Step 3: Add the pure analysis helper**

Create a small helper that inspects a glTF asset for:

- logical skin presence
- whether any primitive exposes both `JOINTS_0` and `WEIGHTS_0`
- the first-pass joint count

- [ ] **Step 4: Re-run the focused test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~GltfSkinningAnalysisTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 3: Add first-pass shader contract coverage for skinning

**Files:**
- Create: `dream.Tests/CubeVertexShaderSkinningContractTests.cs`
- Modify: `dream/shaders/cubeVertex.glsl`

- [ ] **Step 1: Write the failing shader contract test**

Add a file-content test that verifies `cubeVertex.glsl` declares:

- `aJoints`
- `aWeights`
- `useSkinning`
- `jointMatrices`

- [ ] **Step 2: Run the focused shader test and confirm it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CubeVertexShaderSkinningContractTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the current shader does not declare the skinning contract.

- [ ] **Step 3: Extend `cubeVertex.glsl` for first-pass skinning**

Update the vertex shader to:

- read joint and weight attributes
- compute a weighted skin matrix when skinning is enabled
- apply that skin matrix to local position and normal before the model transform

- [ ] **Step 4: Re-run the focused shader test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~CubeVertexShaderSkinningContractTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 4: Upload and apply the joint palette in `AnimatedModel`

**Files:**
- Modify: `dream/AnimatedModel.cs`
- Modify: `dream/Shader.cs`

- [ ] **Step 1: Add a failing test that captures the new pure runtime seam**

Add a focused test for any new pure skinning helper introduced to support palette upload, such as a result type that clamps to the supported joint limit or reports skinning availability.

- [ ] **Step 2: Run the focused test and confirm it fails**

Run the matching `dotnet test` filter for that helper.

Expected: FAIL before implementation.

- [ ] **Step 3: Implement the minimal runtime support**

Extend `AnimatedModel` to:

- detect first-pass skinning data during initialization
- mark primitives that use skinning
- compute joint matrices from `joint.WorldMatrix` and inverse bind matrices
- upload the palette to the shared shader path
- toggle a `useSkinning` uniform per primitive draw

Extend `Shader.cs` only as needed for the cleanest palette upload path.

- [ ] **Step 4: Re-run the focused helper test and affected render tests**

Run:

- the focused new helper test
- `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyRenderStateTests|FullyQualifiedName~SceneNodeTraversalTests|FullyQualifiedName~AnimatedModelRenderSignatureTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 5: Document the first-pass support level and verify the project

**Files:**
- Modify: `docs/rendering-and-assets.md`
- Modify: `docs/architecture.md`
- Modify: `README.md`
- Create: `docs/ledger/work-log/2026-06-29-wolf-asset-rendering-gap.md`

- [ ] **Step 1: Update project docs**

Document:

- the corrected model/node transform composition
- the new first-pass glTF skinning support
- the bounded current support level for skinned assets

- [ ] **Step 2: Add the work-log record**

Create a dated work-log entry summarizing:

- root cause
- implementation shape
- verification commands

- [ ] **Step 3: Run final verification**

Run:

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`

Expected:

- tests PASS
- build PASS with the known BulletSharp warnings still present
