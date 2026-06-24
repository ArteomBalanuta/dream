# Phase 1 Shell Vs Play Scene Work Log

## Summary

Executed the Phase 1 architectural extraction from the game-design pattern roadmap.

`Game` now acts primarily as the OpenTK window shell, while a new `PlayScene` owns active gameplay orchestration.

## Code Changes

- `dream/PlayScene.cs`
  - introduced a dedicated gameplay scene
  - moved ownership of `Render`, `Player`, `Map`, and `PhysicsIntegrator` into the scene
  - moved gameplay update ordering into `PlayScene.Update()`
  - moved gameplay rendering, HUD handling, collision debug rendering, and floor rebuild behavior into `PlayScene.Render()`
  - added scene-specific `Resize()` and `Dispose()` behavior
- `dream/Game.cs`
  - reduced to shell concerns: lifecycle callbacks, cursor application, FPS/title updates, buffer swapping, and scene delegation
- `dream/BulletSharpExtensions.cs`
  - moved Bullet/OpenTK matrix conversion out of `Game.cs` so the helper remains shared after the extraction

## Behavioral Outcome

Preserved current behavior for:

- movement and jump
- mouse look
- settings-menu toggling
- resolution changes
- runtime floor resizing
- collision-debug rendering toggle

## Verification

Commands run:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --nologo
dotnet build dream\dream.csproj --nologo
```

Observed results:

- full test suite passed with `25` tests
- build succeeded
- `dream.exe` launched successfully, reported `Responding = True`, and was then stopped cleanly

## Follow-Up

The next roadmap phase can now target runtime state cleanup on top of a cleaner scene shell rather than inside a monolithic `Game.cs`.
