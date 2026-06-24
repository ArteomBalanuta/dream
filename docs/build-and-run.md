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

### Default controls

- `W`, `A`, `S`, `D`: move
- Mouse movement: look around
- `Space`: jump
- Left mouse button: place a cube
- `F`: toggle frozen movement
- `Esc`: open or close the settings menu

When the settings menu is open, the cursor is released and gameplay input is paused.

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
