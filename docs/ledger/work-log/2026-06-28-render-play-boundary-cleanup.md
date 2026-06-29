# Render Play Boundary Cleanup

Purpose: record the shift of runtime consequence handling out of render-owned code.
Status: completed
Scope: HUD request transport and scene-side application

## Summary

Refactored the HUD interaction path so render-owned code returns user requests while `PlayScene` owns the resulting runtime state changes and gameplay consequences. This restores the currently supported HUD-controlled behaviors for field of view, mouse sensitivity, staged floor size, floor-size apply, collision geometry, and resolution changes through the scene boundary.

## Verification

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`
