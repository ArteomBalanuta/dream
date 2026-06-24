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

Runtime-adjustable window and camera settings are now grouped in `GameSettings.cs`, which currently owns:

- field of view
- mouse sensitivity
- eye-height offset
- move speed
- jump velocity
- current floor size
- pending floor size
- current window width and height
- settings menu visibility

`GameSettings` also defines the bounded runtime floor-size range:

- minimum floor size: `5`
- maximum floor size: `100`

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

## Runtime Settings Flow

`Game.cs` now treats `Esc` as a global settings toggle. When settings are visible:

- the window cursor switches to `Normal`
- gameplay movement input is paused
- cube placement is paused
- ImGui receives live mouse input for the settings panel

When settings are hidden, the cursor is re-grabbed and normal gameplay input resumes.

For floor-size changes, the current flow is:

1. `Render` exposes a `Floor Size` slider and `Apply Floor Size` button.
2. `GameSettings` stages the selected value and clamps it into the supported `5..100` range.
3. `Game` applies the staged size only when the explicit apply action is requested.
4. `Game` rebuilds `Map` with the new square floor dimensions and rebinds the player to the rebuilt world.
5. Existing non-floor cubes are migrated if they remain in bounds; the player position is preserved or clamped.
