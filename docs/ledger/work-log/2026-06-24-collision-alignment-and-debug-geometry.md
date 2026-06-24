# Collision Alignment And Debug Geometry Work Log

## Summary

Implemented a collision-alignment fix and a runtime collision debug overlay.

The work keeps Bullet collision as authoritative and adjusts rendering to match it.

## Code Changes

- `dream/CubeTransforms.cs`
  - added shared helpers for centered visual cube transforms
  - added a helper for Bullet-driven collision debug transforms
- `dream/Render.cs`
  - removed the extra visual centering offset from cube rendering
  - added a collision-geometry checkbox in the settings menu
  - added rendering support for Bullet-driven red collision wireframes
- `dream/GameSettings.cs`
  - added `ShowCollisionGeometry`
  - added a runtime setter for collision debug visibility
- `dream/Game.cs`
  - renders collision wireframes for cube bodies and the player when enabled

## Root Cause Outcome

The cube asset itself was verified to already be centered and unit-sized, so the visible/physical mismatch was traced to the render transform path rather than to the glTF data or the Bullet box shape.

## Tests

Added and passed:

- `dream.Tests/CubeTransformTests.cs`
  - verifies centered visual model transforms keep local origin at cube center
- `dream.Tests/GameSettingsTests.cs`
  - verifies collision debug geometry defaults to hidden
  - verifies collision debug geometry can be enabled

## Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "CubeTransformTests|GameSettingsTests" --nologo
dotnet test dream.Tests\dream.Tests.csproj --nologo
dotnet build dream\dream.csproj --nologo
```

Observed results:

- focused transform/settings tests passed
- full test suite passed with `25` tests
- build succeeded
- `dream.exe` launched successfully, reported `Responding = True`, and was then stopped cleanly

Runtime verification note:

- startup was verified
- the overlay visibility and improved contact feel were not visually confirmed by me inside active gameplay, so those still need human eyes
