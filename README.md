# Dream

`dream` is a small 3D sandbox-style C# project built on OpenTK and BulletSharp. It opens a desktop window, simulates a physics-backed player and voxel grid, and renders cubes, a HUD crosshair, and a simple ImGui overlay.

## Tech Snapshot

- Language: C#
- Framework: .NET 8
- Windowing and OpenGL: OpenTK 4.9
- Physics: BulletSharp
- UI overlay: ImGui.NET
- Model loading: SharpGLTF
- Image loading: SixLabors.ImageSharp

## Prerequisites

- .NET 8 SDK
- Windows desktop environment with OpenGL support
- The repo-owned runtime asset folders included under `dream/`

## Build And Run

From the repository root:

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj
```

The project currently builds successfully with warnings.

The HUD crosshair is now viewport-driven, so it is rebuilt from the live window size instead of from baked pixel coordinates.

## Current Controls

- `W`, `A`, `S`, `D`: move the player
- Mouse movement: look around
- `Space`: jump
- Left mouse button: place a cube in front of the player
- `F`: toggle the frozen movement state
- `Esc`: toggle the settings menu and release or re-grab the cursor

## Settings Menu

The in-game settings panel currently supports:

- field-of-view adjustment
- mouse sensitivity adjustment
- preset resolution switching
- runtime floor-size resizing with an explicit apply step

When the settings menu is open, gameplay movement and cube placement are paused so the UI can safely capture mouse input.

## Runtime Notes

- The main game window is created by `Game` and currently starts at `1440x1024`.
- The title bar is updated with the current FPS count.
- The application grabs the mouse cursor on startup.
- The world now uses separate width, height, and depth bounds instead of one cubic side length.
- The floor is represented visually by solid cubes but physically by one merged static floor collider.
- Changing floor size at runtime rebuilds the square floor as `N x N`, preserves in-bounds placed cubes, and clamps the player back into valid map space if needed.
- The player camera now uses a configurable eye-height offset instead of a fixed close-up offset.
- The HUD crosshair scales with window size and remains centered from the active viewport metrics.
- Future HUD positioning work should derive placement from the live viewport rather than from hardcoded viewport pixel coordinates.
- Runtime assets are resolved from the application output layout through a shared path helper. See [docs/configuration.md](docs/configuration.md) for the current asset-loading model.

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
