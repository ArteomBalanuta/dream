# Configuration Modernization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove machine-specific runtime asset path assumptions from `dream`, make the project runnable across repository locations, and perform bounded structural cleanup that directly supports the fix.

**Architecture:** The implementation will introduce a single shared runtime asset resolution strategy, update the project to model runtime assets explicitly, refactor `Render` and `Map` to consume the shared strategy, and verify the result with both build and runtime execution. Structural cleanup stays constrained to code that currently owns filesystem knowledge or directly benefits from the portability change.

**Tech Stack:** C#/.NET 8, OpenTK, BulletSharp, SharpGLTF, Markdown documentation, MSBuild project content configuration

---

### Task 1: Establish a portable runtime asset strategy

**Files:**
- Create: `dream/AssetPaths.cs`
- Modify: `dream/dream.csproj`
- Read: `dream/Render.cs`
- Read: `dream/Map.cs`

- [ ] **Step 1: Add a shared asset path helper**

Create:

```text
dream/AssetPaths.cs
```

Responsibilities:

- expose a single runtime asset root
- expose helper methods or properties for:
  - shaders
  - animations
  - fonts
- build paths relative to the application runtime location rather than a machine-specific absolute path

- [ ] **Step 2: Decide the runtime root convention**

Implement a convention based on `AppContext.BaseDirectory` so the application resolves assets from the runtime output layout rather than the original developer checkout path.

- [ ] **Step 3: Update the project file to model runtime assets**

Modify `dream/dream.csproj` so the required runtime asset folders are copied to output:

```xml
<ItemGroup>
  <Content Include="shaders\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="animations\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="fonts\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

Also review whether the absolute `Content Remove` entry should be deleted or replaced.

### Task 2: Refactor runtime code to use shared asset resolution

**Files:**
- Modify: `dream/Render.cs`
- Modify: `dream/Map.cs`
- Modify: `dream/Shader.cs`

- [ ] **Step 1: Replace inline shader paths in `Render.cs`**

Change shader loading so `Render` asks the shared helper for:

- default shader paths
- cube shader paths
- HUD shader paths
- ImGui shader paths

- [ ] **Step 2: Replace the inline model path in `Map.cs`**

Change `Map.AddCube()` so `AnimatedModel` is constructed from a shared runtime-resolved path rather than the hardcoded `D:\workspace\dream-project\...` string.

- [ ] **Step 3: Keep `Shader.cs` path-agnostic**

Adjust `Shader.cs` only if needed so it works cleanly with resolved paths, but avoid pushing machine-specific logic into it.

### Task 3: Apply bounded structure cleanup where it helps the fix

**Files:**
- Modify: `dream/Render.cs`
- Optional Create: focused helper files if needed

- [ ] **Step 1: Reduce asset-configuration knowledge inside `Render.cs`**

Extract or simplify whichever part of `Render` currently makes asset-path ownership harder to follow. Acceptable examples:

- a small shader setup helper
- a grouped initialization method split
- a lightweight HUD/ImGui separation if it directly improves clarity

- [ ] **Step 2: Keep cleanup bounded**

Do not expand this into a full rendering rewrite. Stop once filesystem/configuration responsibilities are clearly separated enough to support the portability fix.

### Task 4: Update docs and ledger to reflect the new configuration model

**Files:**
- Modify: `README.md`
- Modify: `docs/configuration.md`
- Modify: `docs/rendering-and-assets.md`
- Create: `docs/ledger/work-log/YYYY-MM-DD-configuration-modernization.md`

- [ ] **Step 1: Update user-facing configuration docs**

Remove or revise statements that describe hardcoded absolute path assumptions once the code no longer depends on them.

- [ ] **Step 2: Record the implementation in the ledger**

Create a work-log entry describing:

- what asset strategy was introduced
- which files changed
- what was verified

### Task 5: Verify build, output content, and runtime startup

**Files:**
- Test: `dream/dream.csproj`
- Test: `dream/AssetPaths.cs`
- Test: `dream/Render.cs`
- Test: `dream/Map.cs`
- Test: `README.md`
- Test: `docs/configuration.md`
- Test: `docs/rendering-and-assets.md`

- [ ] **Step 1: Verify the build**

Run:

```powershell
dotnet build dream\dream.csproj
```

Expected:

- `Build succeeded.`
- existing warnings may remain unless explicitly addressed

- [ ] **Step 2: Verify asset output**

Run checks such as:

```powershell
Get-ChildItem dream\bin\Debug\net8.0\shaders
Get-ChildItem dream\bin\Debug\net8.0\animations
Get-ChildItem dream\bin\Debug\net8.0\fonts
```

Expected:

- required runtime assets are present in output

- [ ] **Step 3: Verify runtime startup**

Run:

```powershell
dotnet run --project dream\dream.csproj
```

Expected:

- application starts without failing on missing absolute shader/model paths

- [ ] **Step 4: Re-check the scope boundary**

Confirm that the change solved portability and runtime startup without turning into a broad uncontrolled refactor.
