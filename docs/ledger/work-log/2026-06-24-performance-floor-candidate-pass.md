# Performance Floor Candidate Pass Work Log

## Summary

Implemented a dedicated floor render-candidate path so the static base floor no longer travels through the same candidate-selection path as placed cubes.

## Change

- kept the placed-cube spatial index focused on non-floor cubes
- added `Map.GetRenderCandidateCubes(...)`
- queried nearby floor tiles directly from local map bounds
- combined those floor tiles with nearby placed cubes for the render path

## Scope Boundary

This pass does **not** introduce:

- floor mesh generation
- GPU instancing
- a separate floor shader or renderer
- dynamic floor mutation support

It is a bounded candidate-selection optimization aligned to the current static floor model.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter MapRenderCandidateTests --nologo`
- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

## Expected Effect

Large floors should now scale better because the static floor is handled as a direct local-grid query while the spatial index is reserved for placed cubes.
