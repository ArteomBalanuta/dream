# Configuration Modernization Design

**Purpose:** Define the intended target state for improving runtime asset configuration, portability, and related project structure in `dream`.

**Status:** Drafted from confirmed analysis

**Scope:** Runtime asset lookup, project content configuration, and bounded structural cleanup around rendering and asset loading

## Goal

Make the project portable and runnable across repository locations by removing machine-specific asset path assumptions, introducing a single runtime asset resolution model, and performing targeted structural cleanup where it directly supports maintainability.

## Design Principles

- Fix runtime correctness before broader modernization.
- Centralize asset-location logic instead of scattering string paths.
- Prefer small, explicit helpers over implicit filesystem assumptions.
- Keep structural cleanup bounded to areas that materially support the configuration fix.
- Preserve current behavior as much as possible while changing how resources are located.

## Target State

After the work is complete:

- the project no longer depends on `D:\workspace\dream-project\...` paths
- shader, model, texture, and font assets are treated as project-owned runtime content
- runtime classes resolve assets through a shared, portable mechanism
- `dotnet run --project dream\dream.csproj` works from this repository checkout
- the code no longer mixes as much filesystem configuration detail into gameplay/rendering classes

## Desired Runtime Asset Model

### Asset ownership

The following folders are part of the application runtime:

- `dream/shaders/`
- `dream/animations/`
- `dream/fonts/`

### Resolution strategy

The runtime should locate assets relative to a single root, preferably the application base directory or a clearly derived path from it.

The application should not:

- hardcode machine-specific drive letters
- assume the repository is checked out in one exact location
- duplicate path-construction logic across multiple classes

### Project file behavior

The project file should explicitly declare the runtime asset directories so the output folder contains the files required at runtime.

## Component Design

### New shared asset locator

Introduce a small shared component such as:

- `AssetPaths`
- `AssetLocator`
- `RuntimeAssetResolver`

Responsibilities:

- build portable paths for shaders, animations, and fonts
- expose intent-revealing helpers rather than raw string concatenation
- keep filesystem assumptions in one place

Expected consumers:

- `Render`
- `Map`
- any future runtime asset loader

### `Render` changes

`Render` should stop constructing shader paths inline. It should ask the shared asset locator for:

- HUD shader paths
- ImGui text shader paths
- world shader paths

If a structural split is helpful during the work, acceptable extraction targets include:

- shader-loading setup
- HUD rendering
- ImGui rendering resources

### `Map` changes

`Map` should stop embedding the exact glTF model path inline. It should request the model path from the same shared asset locator used elsewhere.

### `Shader` changes

`Shader` does not necessarily need a redesign, but it should operate on already-resolved portable paths and remain agnostic about machine layout.

### `dream.csproj` changes

The project file should:

- explicitly include runtime asset directories as content when needed
- copy required assets into output consistently
- avoid environment-specific project entries where they are not necessary

## Structural Cleanup Boundaries

The design allows targeted structural improvements, but they must remain bounded.

### Included

- extracting shared asset resolution
- reducing path knowledge inside `Render` and `Map`
- tightening runtime asset declarations in `dream.csproj`
- modest splits in `Render.cs` if they directly improve clarity during the configuration fix

### Deferred

- full rendering architecture rewrite
- major gameplay refactors
- deep decomposition of `AnimatedModel.cs` unless required by the portability implementation

## Verification Requirements

The implementation should be considered successful only if it verifies:

1. `dotnet build dream\dream.csproj` succeeds
2. `dotnet run --project dream\dream.csproj` no longer fails because of missing hardcoded asset paths
3. the runtime assets referenced by the application are present where the application expects them
4. documentation and ledger records are updated to reflect the new configuration model

## Risks And Guardrails

### Risks

- changing asset layout may break glTF-relative file references if done carelessly
- moving too much rendering code at once increases regression risk
- fixing project content rules incorrectly may work in one environment but fail in another

### Guardrails

- keep the first pass focused on asset portability and bounded cleanup
- verify the actual runtime startup path, not just compilation
- preserve existing folder names unless there is a strong reason to change them

## Success Condition

The modernization is successful when a future agent or human can clone the repository in a different location, run the documented commands, and not depend on the original developer's absolute machine paths.
