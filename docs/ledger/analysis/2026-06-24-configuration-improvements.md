# Configuration Improvements Analysis

**Purpose:** Document the current configuration and runtime asset-loading problems in `dream`, identify confirmed root causes, and capture improvement opportunities for portability and project structure.

**Status:** Complete

**Scope:** Runtime asset resolution, project content configuration, and structure improvements directly related to portability and maintainability

## Summary

The current project builds successfully but does not start successfully in this workspace. The immediate blocker is runtime asset resolution through hardcoded absolute Windows paths in application code. The repository contains the required shader and model assets, but the code does not resolve them relative to the repository or application base directory.

This is a configuration and portability problem first. It also exposes a maintainability problem: asset-path logic is scattered across multiple runtime classes rather than being centralized.

## Evidence Gathered

### Build status

Command run:

```powershell
dotnet build dream\dream.csproj
```

Observed result:

- Build succeeds
- Warnings remain for `BulletSharp`, `SixLabors.ImageSharp`, nullable issues, and obsolete APIs

### Runtime status

Command run:

```powershell
dotnet run --project dream\dream.csproj
```

Observed result:

- Startup fails at runtime
- Exception thrown by `SharpGLTF.Schema2.ModelRoot.Load()`
- Failing path:

```text
D:\workspace\dream-project\dream\animations\GrassTextureCube.gltf
```

### Hardcoded path locations found in source

Confirmed absolute path references:

- `dream/Map.cs`
  - `D:\workspace\dream-project\dream\animations\GrassTextureCube.gltf`
- `dream/Render.cs`
  - `D:\workspace\dream-project\dream\shaders\hudVertex.glsl`
  - `D:\workspace\dream-project\dream\shaders\hudFragment.glsl`
  - `D:\workspace\dream-project\dream\shaders\hudVertexText.glsl`
  - `D:\workspace\dream-project\dream\shaders\hudFragmentText.glsl`
  - `D:\workspace\dream-project\dream\shaders\vertexShader.glsl`
  - `D:\workspace\dream-project\dream\shaders\fragmentShader.glsl`
  - `D:\workspace\dream-project\dream\shaders\cubeVertex.glsl`
  - `D:\workspace\dream-project\dream\shaders\cubeFragment.glsl`

### Project content observations

The repository already contains:

- shader files under `dream/shaders/`
- model and texture assets under `dream/animations/`
- a font file under `dream/fonts/`

The current `dream.csproj`:

- targets `.NET 8`
- has no portable asset-path strategy
- does not explicitly model the runtime asset directories as content to copy for deployment
- contains an absolute `Content Remove` path referencing a local NuGet cache path

## Confirmed Root Causes

### 1. Runtime asset resolution is hardcoded to one developer machine layout

The code assumes assets live under:

```text
D:\workspace\dream-project\dream\...
```

This assumption is false in the current repository checkout:

```text
G:\workspace\projects\dream\...
```

Because the path mismatch occurs before the application finishes initialization, startup fails immediately.

### 2. Asset lookup is decentralized and string-literal based

`Map` and `Render` each construct their own absolute asset paths inline. There is no shared asset resolution helper, configuration object, or repository-relative resource convention.

This makes it easy for asset paths to drift and hard to change the runtime layout safely.

### 3. Project content configuration does not support a clear deployment model

The current project runs in development mostly by relying on repository-local files and hardcoded assumptions. The `.csproj` does not clearly declare which runtime assets should be copied into the output directory or how the program should locate them after build/publish.

### 4. Documentation currently describes the problem but the code still embodies it

The repository documentation already records the hardcoded-path behavior accurately, but the implementation still depends on those paths. This means the docs are correct, but the project remains non-portable.

## Immediate Remediation Needs

These are the changes most directly required to make the project runnable across environments:

1. Replace hardcoded absolute paths with a repo-relative or app-base-relative asset resolution strategy.
2. Centralize asset path construction in one shared component.
3. Update `dream.csproj` so required runtime assets are present where the application expects them.
4. Verify portability with `dotnet run --project dream\dream.csproj`.

## Recommended Target-State Configuration Model

The cleanest portable model for this repository is:

- treat `dream/shaders/`, `dream/animations/`, and `dream/fonts/` as project-owned runtime assets
- ensure they are copied into the build output
- resolve paths relative to `AppContext.BaseDirectory` or another single runtime root
- keep runtime classes unaware of machine-specific absolute paths

This would let the project run from:

- the repo root via `dotnet run`
- the output directory after build
- future environments that check out the repo elsewhere

## Structural Improvement Opportunities

These opportunities are real and relevant, but they should be prioritized separately from the immediate runtime fix.

### High-value targeted cleanup

- Introduce a shared `AssetPaths` or `AssetLocator` helper used by `Render`, `Map`, and any future asset consumers.
- Move shader/model path knowledge out of rendering/gameplay classes.
- Tighten `.csproj` content declarations around runtime assets.

### Medium-value structure cleanup

- Split `Render.cs` into smaller responsibilities:
  - world rendering
  - HUD rendering
  - ImGui resource management
- Reduce direct constructor-time resource loading where it obscures failure handling.

### Lower-priority structural cleanup

- Consider whether `AnimatedModel.cs` should later be split into:
  - model loading
  - material/texture setup
  - rendering

This file is large, but it is not the first place to refactor unless the portability fix naturally requires it.

## Specific Project-Structure Concerns

### `Render.cs`

- Large file
- Mixed responsibilities
- Contains direct shader-path knowledge
- Owns world rendering, HUD rendering, and ImGui resource setup together

### `Map.cs`

- Owns world-grid logic and also knows exact model asset location
- This couples world construction to filesystem details

### `dream.csproj`

- Lacks a clear runtime-asset model
- Contains an absolute `Content Remove` path that is environment-specific and suspicious from a portability perspective

## Proposed Modernization Strategy

The best modernization path is two-track:

### Track A: Portability and runtime correctness

- centralize asset resolution
- convert runtime asset loading to relative paths
- declare/copy runtime assets explicitly in the project
- verify that `dotnet run` works

### Track B: Bounded structure cleanup

- extract asset-location concerns out of `Render` and `Map`
- optionally split `Render` into smaller focused units if it directly helps the portability work
- defer larger refactors unless they are required by Track A

## Out Of Scope For The First Pass

The first modernization pass does not need to solve:

- every compiler warning
- every obsolete API usage
- every large-file decomposition in the codebase
- full architecture redesign

Those are valid future tasks, but they should not block the configuration fix.

## Recommended Next Artifacts

- A spec defining the intended portable configuration model
- An implementation plan for replacing hardcoded asset paths and tightening project content handling
- A work log when implementation starts or completes
