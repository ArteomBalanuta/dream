# World Asset Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace ad hoc oversized imported-world-model scaling with explicit asset normalization metadata that derives in-game render scale from authored size.

**Architecture:** Add a small pure normalization type that converts authored height to target world height, then route wolf rendering through that metadata instead of through a raw hand-tuned scale constant. Keep the change bounded to asset-definition and world-render transform seams.

**Tech Stack:** C# / .NET 10, OpenTK, xUnit

---

### Task 1: Add a pure normalization seam

**Files:**
- Create: `dream/WorldAssetNormalization.cs`
- Create: `dream.Tests/WorldAssetNormalizationTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void ResolveUniformScale_Returns_TargetHeight_Divided_By_AuthoredHeight()
{
    WorldAssetNormalization normalization = new(3.191f, 1.20f);

    Assert.Equal(1.20f / 3.191f, normalization.ResolveUniformScale(), 3);
}
```

- [ ] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WorldAssetNormalizationTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the type does not exist yet.

- [ ] **Step 3: Implement the minimal normalization type**

Create a pure type that stores:

- authored height
- target world height

and exposes a derived uniform scale method.

- [ ] **Step 4: Re-run the focused test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WorldAssetNormalizationTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 2: Migrate the wolf definition to normalization metadata

**Files:**
- Modify: `dream/WolfAssetDefinition.cs`
- Modify: `dream.Tests/WolfAssetDefinitionTests.cs`

- [ ] **Step 1: Write the failing wolf metadata test**

Extend the wolf-definition tests to assert:

- authored height is `3.191f`
- target height is `1.20f`
- derived scale is approximately `0.376`

- [ ] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WolfAssetDefinitionTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL because the metadata does not exist yet.

- [ ] **Step 3: Implement the wolf normalization metadata**

Replace the raw `VisualScale` constant with a reusable normalization entry and expose a derived render scale from it.

- [ ] **Step 4: Re-run the focused test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~WolfAssetDefinitionTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 3: Route world render state through normalized scale

**Files:**
- Modify: `dream/EnemyRenderState.cs`
- Modify: `dream.Tests/EnemyRenderStateTests.cs`

- [ ] **Step 1: Write the failing render-state test**

Add a test that verifies the default wolf render state uses the derived normalized scale rather than the old raw constant.

- [ ] **Step 2: Run the focused test to verify it fails**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyRenderStateTests" --nologo /p:UseSharedCompilation=false`

Expected: FAIL before the render state uses normalization metadata.

- [ ] **Step 3: Implement the render-state change**

Update `EnemyRenderState.CreateWolf(...)` so the default scale comes from the wolf normalization metadata.

- [ ] **Step 4: Re-run the focused test**

Run: `dotnet test dream.Tests\\dream.Tests.csproj --filter "FullyQualifiedName~EnemyRenderStateTests" --nologo /p:UseSharedCompilation=false`

Expected: PASS.

### Task 4: Document and verify

**Files:**
- Modify: `README.md`
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/2026-06-29-world-asset-normalization.md`

- [ ] **Step 1: Update docs**

Document that world assets now declare target world size through normalization metadata instead of relying only on opaque scale constants.

- [ ] **Step 2: Add the work-log entry**

Record the authored wolf height, target world height, and derived scale.

- [ ] **Step 3: Run final verification**

Run:

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`

Expected:

- tests PASS
- build PASS with the known BulletSharp warning still present
