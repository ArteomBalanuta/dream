# Dream

`dream` is a small 3D sandbox-style C# project built on OpenTK and BulletSharp. It opens a desktop window, simulates a physics-backed player and voxel grid, and renders cubes, a HUD crosshair, and a simple ImGui overlay.

## Tech Snapshot

- Language: C#
- Framework: .NET 10
- Windowing and OpenGL: OpenTK 4.9
- Physics: BulletSharp
- UI overlay: ImGui.NET
- Model loading: SharpGLTF
- Image loading: SixLabors.ImageSharp

## Prerequisites

- .NET 10 SDK
- Windows desktop environment with OpenGL support
- The repo-owned runtime asset folders included under `dream/`

The repository now includes `global.json`, so matching the pinned SDK version is recommended for the most predictable restore/build behavior.

## Build And Run

From the repository root:

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj
```

The project currently builds successfully with warnings. The remaining package warning comes from `BulletSharp`, which is intentionally still in use and restores as an older .NET Framework-oriented package.

The HUD crosshair is now viewport-driven, so it is rebuilt from the live window size instead of from baked pixel coordinates.

## Current Controls

- `W`, `A`, `S`, `D`: move the player
- Mouse movement: look around
- `Space`: jump
- `1`: select building mode
- `2`: select combat mode
- Left mouse button: perform the active mode's primary action
- `V`: toggle noclip/fly mode
- `F`: toggle the frozen movement state
- `Esc`: toggle the settings menu and release or re-grab the cursor

## Settings Menu

The in-game settings panel currently supports:

- field-of-view adjustment
- mouse sensitivity adjustment
- noclip/fly-speed adjustment
- runtime `Strength`, `Attack Speed`, and `Vitality` tuning
- runtime floating damage text size, speed, and crit-depth tuning
- preset resolution switching
- runtime floor-size resizing with an explicit apply step
- collision-geometry debug toggling for cubes and player

When the settings menu is open, gameplay movement and cube placement are paused so the UI can safely capture mouse input.

## Runtime Notes

- `Game` now acts as the OpenTK window shell, while `PlayScene` owns active gameplay orchestration.
- The main game window is created by `Game` and currently starts at `1440x1024`.
- The title bar is updated with the current FPS count.
- The application grabs the mouse cursor on startup.
- The scene now spawns one first-pass wolf enemy that chases the player, attacks in melee range, and can be killed in combat mode.
- If no active wolf enemies remain during gameplay, the scene respawns the default wolf.
- World-model transform composition now respects glTF node-local transforms correctly, so authored mesh-node scale does not shove actor instances off-map.
- The shared animated-model pipeline now includes a first-pass glTF skinning path for skinned creature assets such as the wolf.
- The world now uses separate width, height, and depth bounds instead of one cubic side length.
- The floor is represented visually by solid cubes but physically by one merged static floor collider.
- Changing floor size at runtime rebuilds the square floor as `N x N`, preserves in-bounds placed cubes, and clamps the player back into valid map space if needed.
- Large-floor rendering now applies a lightweight visibility gate before per-cube drawing, reducing obviously wasted draws for distant or behind-camera cubes.
- World rendering now also queries nearby spatial sectors before running that visibility gate, so candidate iteration scales better with larger floors.
- Visible cubes that share the same animated model and shader are now batched through a shared setup path, reducing repeated per-cube model-render overhead.
- The base floor now uses a dedicated local candidate query, while the spatial index is reserved for placed cubes, reducing generic candidate overhead on larger maps.
- The player camera now uses a configurable eye-height offset instead of a fixed close-up offset.
- The HUD crosshair scales with window size and remains centered from the active viewport metrics.
- The existing top-left gameplay HUD now shows player HP, active interaction mode, and current derived damage range.
- Combat stats are now player-owned runtime values instead of a scene-local random damage roll.
- Combat mode can now damage destructible placed cubes, while the base floor remains protected in this first pass.
- Combat mode now targets enemies before destructible cubes when the crosshair ray resolves a valid wolf hit.
- Cube visuals now follow the centered unit-cube collision model instead of applying an extra visual offset.
- A debug collision overlay can render red wireframes for cube and player collision bodies from Bullet transforms.
- Future HUD positioning work should derive placement from the live viewport rather than from hardcoded viewport pixel coordinates.
- Runtime assets are resolved from the application output layout through a shared path helper. See [docs/configuration.md](docs/configuration.md) for the current asset-loading model.
- The first enemy asset is a repo-owned animated wolf-compatible `.glb` under `dream/animations/enemies/wolf.glb`.
- Current skinned-model support is intentionally first-pass: one logical skin per asset is supported cleanly, which is enough for the current wolf milestone.
- Imported world assets can now declare authored-height and target-world-height normalization metadata so render scale is derived from explicit sizing intent instead of from opaque one-off constants.

## Project Layout

- `dream/`: main project source, shaders, fonts, and assets
- `docs/`: project documentation
- `docs/ledger/`: analysis, planning, decisions, and work records for ongoing development

## Further Reading

- [Build and run guide](docs/build-and-run.md)
- [Project overview](docs/overview.md)
- [Architecture](docs/architecture.md)
- [Project structure](docs/project-structure.md)
- [Configuration](docs/configuration.md)
- [Rendering and assets](docs/rendering-and-assets.md)
