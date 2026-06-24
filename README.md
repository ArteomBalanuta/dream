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
- Access to the asset and shader files expected by the project

## Build And Run

From the repository root:

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj
```

The project currently builds successfully with warnings.

## Current Controls

- `W`, `A`, `S`, `D`: move the player
- Mouse movement: look around
- `Space`: jump
- Left mouse button: place a cube in front of the player
- `F`: toggle the frozen movement state

## Runtime Notes

- The main game window is created by `Game` and currently starts at `1440x1024`.
- The title bar is updated with the current FPS count.
- The application grabs the mouse cursor on startup.
- Runtime assets are resolved from the application output layout through a shared path helper. See [docs/configuration.md](docs/configuration.md) for the current asset-loading model.

## Project Layout

- `dream/`: main project source, shaders, fonts, and assets
- `docs/`: project documentation
- `docs/ledger/`: analysis, planning, decisions, and work records for ongoing development

## Further Reading

- [Project overview](docs/overview.md)
- [Architecture](docs/architecture.md)
- [Project structure](docs/project-structure.md)
- [Configuration](docs/configuration.md)
- [Rendering and assets](docs/rendering-and-assets.md)
