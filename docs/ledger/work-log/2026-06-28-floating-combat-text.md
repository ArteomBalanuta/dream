# Floating Combat Text

Date: 2026-06-28
Status: Completed first vertical slice
Owner: Codex

## Summary

Implemented the first project-fit floating combat text slice based on `DMG_IMPL.md`.

This pass focuses on:

- pooled transient entries
- burst / float / fade lifecycle math
- screen-space projection from world anchors
- bounded active entries per target
- one controlled demo spawn path for validation

## What Changed

- Added `FloatingCombatTextSystem` to own pooled entry runtime state.
- Added motion helpers and style definitions for regular and incoming damage.
- Integrated rendering through the existing HUD path instead of introducing a world-canvas system.
- Added a demo regular-damage spawn on primary use so the feel can be checked in runtime immediately.
- Added focused tests for lifecycle motion and cap behavior.

## Integration Shape

- `PlayScene` triggers the current demo spawn on primary use.
- `HudRenderer` owns the floating text runtime system and advances it per frame.
- `Render.DrawHUD()` projects the world anchors into screen space and draws the text through ImGui draw lists.

## Verification

Commands used during implementation:

```powershell
dotnet test dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~FloatingCombatText" --nologo
dotnet build dream\dream.csproj --nologo
dotnet test dream.Tests\dream.Tests.csproj --nologo
```

Notes:

- focused floating-combat-text tests passed
- the repo still exhibits its known intermittent `dream.dll` file-lock behavior during back-to-back builds, so verification should be run sequentially

## Next Recommended Step

Wire the future floating-text spawn source to the upcoming creature support layer instead of the current demo primary-use trigger.
