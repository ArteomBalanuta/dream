# Build And Run

## Prerequisites

- `.NET 10 SDK`
- Windows desktop environment with OpenGL support

Run the commands in this guide from the repository root:

```powershell
G:\workspace\projects\dream
```

## Build

Use:

```powershell
dotnet build dream\dream.csproj
```

Current expected result:

- build succeeds
- warnings are still present

## Run

Use:

```powershell
dotnet run --project dream\dream.csproj
```

The project copies runtime assets into the build output and resolves them through `AssetPaths`, so shader, animation, and font files are loaded from the runtime output layout rather than from machine-specific absolute paths.

## Default Runtime Behavior

On startup, the game currently:

- opens a `1440 x 1024` window
- grabs the mouse cursor for camera look
- starts with a `70` degree field of view
- builds the HUD crosshair from the live viewport instead of from fixed HUD pixel coordinates
- scales the HUD crosshair with window size while keeping it centered on the active viewport
- starts with a `25 x 25` floor that can be resized at runtime from the settings menu
- loads the default first-person hands and held-item blockout viewmodels from `dream/animations/viewmodels/`

### Default controls

- `W`, `A`, `S`, `D`: move
- Mouse movement: look around
- `Space`: jump
- Left mouse button: place a cube
- `F`: toggle frozen movement
- `Esc`: open or close the settings menu

When the settings menu is open, the cursor is released and gameplay input is paused.

## Runtime Verification For Viewmodels

Use:

```powershell
dotnet run --project dream\dream.csproj
```

Current first-milestone verification flow:

1. Start the app and keep the settings menu closed so gameplay input is active.
2. Confirm the blockout hands and held item are visible in front of the camera.
3. Move and look around to confirm the world stays behind the first-person pass.
4. Left click to trigger the `Use` animation on the hands and held item.
5. Confirm the models settle back into their looping `Idle` presentation after the one-shot finishes.
6. Confirm the HUD crosshair and ImGui still render on top of the first-person pass.

Success for this milestone means:

- both first-person slots render every frame
- the pass uses live viewport aspect without obvious stretching after resize
- left click visibly triggers the one-shot use motion
- the world and debug passes remain behind the hands and held item
- the HUD remains the top-most layer

## Runtime Floor Resize

Open the settings menu with `Esc`, move the `Floor Size` slider, and press `Apply Floor Size`.

Current behavior:

- the slider range is `5` to `100`
- the value represents a square `N x N` floor
- the world is rebuilt in place when you apply the change
- non-floor cubes are preserved only if they still fit inside the rebuilt bounds
- the player position is preserved when possible and otherwise clamped back into valid space

## Test

Use:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

This validates the shared asset-path and shared resource-cache behavior currently covered by the test project.
It also covers the shared `GameSettings` defaults and resolution update behavior.
It now also covers floor-size staging, apply behavior, and floor-size bounds.
It also covers the viewmodel catalog, runtime controller, and named clip playback helpers added for the first-person foundation.
For the HUD crosshair follow-up, the focused verification command used so far is `dotnet test dream.Tests\dream.Tests.csproj --filter HudLayoutTests --nologo`, which passed with `6` tests.

## Known Warnings

The project currently builds with warnings, including:

- `BulletSharp 0.11.1` compatibility warnings against `net10.0`

Warnings are currently expected. Build failures are the condition that should block progress.

## Runtime Assets

The runtime asset folders are:

- `dream/shaders/`
- `dream/animations/`
- `dream/fonts/`

These directories are copied into the output under:

- `dream/bin/Debug/net10.0/shaders/`
- `dream/bin/Debug/net10.0/animations/`
- `dream/bin/Debug/net10.0/fonts/`

If runtime asset loading fails, verify those output folders exist after build.

## Local Generated Files

The repo may generate local runtime artifacts such as:

- `imgui.ini`
- `dream-run*.log`

These are ignored by git and should not be treated as source files.

## Troubleshooting

### `.NET` SDK missing

If `dotnet` commands fail before build starts, verify that `.NET 10 SDK` is installed and available in `PATH`.

### Build warnings vs errors

Warnings are currently expected. If the build exits with non-zero status or shows actual compile errors, treat that as a project problem rather than normal state.

### Runtime assets missing from output

If the application starts failing on missing shader or model files again:

1. Run `dotnet build dream\dream.csproj`
2. Check the output directories under `dream/bin/Debug/net10.0/`
3. Confirm `shaders/`, `animations/`, and `fonts/` are present
4. If the first-person pass is missing assets, confirm `animations/viewmodels/DebugHands.gltf` and `animations/viewmodels/DebugHeldItem.gltf` were copied into the output tree

### Tests for shared runtime helpers

If asset-path or shared-model behavior changes, rerun:

```powershell
dotnet test dream.Tests\dream.Tests.csproj
```

### Movement or camera feels wrong

If the app starts but movement or camera feel off:

1. Press `Esc` to open the settings menu
2. Adjust `Field of View` or `Mouse Sensitivity`
3. If needed, change the resolution preset and re-test movement feel

The current player camera uses a configurable eye-height offset and a viewport-aware projection matrix. At runtime, the in-app controls exposed through the settings menu are `Field of View`, `Mouse Sensitivity`, and the resolution presets.
The same menu now also exposes `Floor Size` with an explicit apply action, rebuilding the map without discarding cubes that still fit.
The HUD crosshair now follows the live viewport as well, so future HUD placement changes should be driven from viewport-derived layout values rather than hardcoded viewport pixel coordinates.

## Pending Runtime Verification

Live visual verification of the viewport-driven crosshair at multiple window sizes is still pending and should be handled separately if a change needs end-to-end runtime confirmation.

The first-person milestone was manually checked with `dotnet run --project dream\dream.csproj`; screenshots were kept locally under `artifacts/` for review and were not committed to the repository.
