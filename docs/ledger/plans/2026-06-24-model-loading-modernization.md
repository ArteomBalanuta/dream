# Model Loading Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reuse shared model resources and reduce startup log noise in `dream` without expanding into a broad rendering rewrite.

**Architecture:** The implementation will add a small reusable resource cache, build a focused shared model access layer on top of it, update `Map` to consume the shared floor model instead of constructing one per cube, and gate or remove verbose startup logging in model-loading hot paths.

**Tech Stack:** C#/.NET 8, OpenTK, SharpGLTF, Markdown documentation, xUnit tests

---

### Task 1: Add a reusable cache with test-first coverage

**Files:**
- Create: `dream/ResourceCache.cs`
- Create: `dream.Tests/ResourceCacheTests.cs`

- [ ] **Step 1: Write failing cache tests**

Cover at least:

- same key returns the same created instance
- factory runs only once per key

- [ ] **Step 2: Run the tests to verify they fail for the missing cache type**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

- [ ] **Step 3: Implement the minimal cache**

Add a small generic cache that supports keyed lazy reuse.

- [ ] **Step 4: Re-run tests to verify they pass**

Run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

### Task 2: Introduce shared model access and use it in `Map`

**Files:**
- Create: `dream/ModelAssets.cs`
- Modify: `dream/Map.cs`

- [ ] **Step 1: Add a focused shared model provider**

Provide a shared accessor for the grass cube model using the new cache and `AssetPaths.Animation("GrassTextureCube.gltf")`.

- [ ] **Step 2: Update `Map` to reuse the shared model**

Replace per-cube `new AnimatedModel(...)` construction with shared model access.

### Task 3: Reduce noisy startup logging

**Files:**
- Modify: `dream/AnimatedModel.cs`
- Modify: `dream/Map.cs`

- [ ] **Step 1: Gate or remove verbose loader diagnostics**

Remove or wrap the exploratory `Console.WriteLine` calls in:

- material loading
- mesh loading
- vertex logging
- animation loading

- [ ] **Step 2: Remove or gate per-cube startup physics logs**

Stop printing `Adding cube at ...` for every floor cube during normal execution.

### Task 4: Update docs and ledger

**Files:**
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/2026-06-24-model-loading-modernization.md`

- [ ] **Step 1: Update docs to describe shared model reuse**

- [ ] **Step 2: Record the implementation and verification**

### Task 5: Verify build, tests, and quieter runtime startup

**Files:**
- Test: `dream.Tests/dream.Tests.csproj`
- Test: `dream/dream.csproj`
- Test: `dream/Map.cs`
- Test: `dream/AnimatedModel.cs`

- [ ] **Step 1: Run unit tests**

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

- [ ] **Step 2: Run build verification**

```powershell
dotnet build dream\dream.csproj
```

- [ ] **Step 3: Run runtime startup verification**

```powershell
dotnet run --project dream\dream.csproj
```

Confirm:

- startup does not fail
- output is materially quieter than before
- the change remains bounded
