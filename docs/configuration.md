# Configuration

## Project Target

The project file `dream/dream.csproj` currently targets:

- `net8.0`

Other enabled project settings include:

- `ImplicitUsings` enabled
- `Nullable` enabled
- `AllowUnsafeBlocks` enabled

## Package References

The current NuGet package references are:

- `BulletSharp` `0.11.1`
- `ImGui.NET` `1.91.6.1`
- `OpenTK` `4.9.3`
- `OpenTK.Windowing.Desktop` `4.9.3`
- `SharpGLTF.Core` `1.0.3`
- `SixLabors.ImageSharp` `3.1.6`
- `System.Drawing.Common` `9.0.1`

## Build Verification

The project was verified with:

```powershell
dotnet build dream\dream.csproj
```

Result:

- Build succeeded
- The build produced warnings, including package compatibility and vulnerability warnings

The current warnings include:

- `BulletSharp 0.11.1` restored from older .NET Framework targets instead of `net8.0`
- `SixLabors.ImageSharp 3.1.6` vulnerability warnings
- Several nullable and obsolete API warnings in the source files

## Window Configuration

The current `Game` constructor creates a window with:

- Title: `Physics Integrated Game`
- Initial size: `1440 x 1024`

The project also grabs the cursor on startup.

## Runtime Asset Path Assumptions

Runtime assets are now treated as project-owned content and resolved relative to the application runtime directory.

### Runtime asset folders

- `shaders/`
- `animations/`
- `fonts/`

### Resolution model

`AssetPaths.cs` builds runtime paths from `AppContext.BaseDirectory`, giving the application one portable asset root.

Current consumers include:

- `Render.cs` for shader file paths
- `Map.cs` for the `GrassTextureCube.gltf` model path

### Output-copy behavior

`dream.csproj` now copies files from:

- `shaders\**\*`
- `animations\**\*`
- `fonts\**\*`

into the build output so the runtime asset layout matches the resolution model.

## Project Content Layout

The project file includes:

- Removal rules for an `animations\BlueCubeGenerator\**` subtree
- runtime asset copy rules for `shaders`, `animations`, and `fonts`

## Input And Frame Assumptions

The project currently expects:

- Keyboard input from OpenTK `KeyboardState`
- Mouse input from OpenTK `MouseState`
- A per-frame physics step driven from `OnUpdateFrame`
- Per-frame HUD drawing driven from `OnRenderFrame`
