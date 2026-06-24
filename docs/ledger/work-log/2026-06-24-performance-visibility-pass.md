# Performance Visibility Pass Work Log

## Summary

Implemented a lightweight world-visibility filter to reduce obvious wasted world draws on larger floors.

## Change

- added `WorldVisibility` as a cheap cube-visibility helper
- routed `WorldRenderer` through the helper before calling `Render.DrawCube()`
- added focused tests for in-front, out-of-range, behind-camera, and near-camera behavior

## Scope Boundary

This pass does **not** introduce:

- chunk meshing
- GPU instancing
- merged floor meshes
- debug-rendering optimization

It is intentionally a bounded culling pass meant to improve the current renderer without rewriting it.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter WorldVisibilityTests --nologo`
- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

## Expected Effect

The biggest benefit should appear on larger floors, where many cubes are either far outside the local play area or behind the player camera and therefore no longer need to be drawn each frame.
