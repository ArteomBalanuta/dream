# Performance Model Batching Pass Work Log

## Summary

Implemented a CPU-side shared-model batching pass so visible cubes that use the same `AnimatedModel` and shader no longer repeat top-level render setup for every single cube.

## Change

- added `WorldRenderBatchPlanner`
- grouped visible cubes by shared animation reference and shader name
- split `AnimatedModel.Render()` into reusable begin/render/end phases
- routed `WorldRenderer` through the batch planner

## Scope Boundary

This pass does **not** introduce:

- GPU instancing
- merged world meshes
- floor-specific mesh generation
- material deduplication beyond the existing model/shader grouping

It is a bounded batching pass inside the current renderer model.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --filter WorldRenderBatchPlannerTests --nologo`
- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

## Expected Effect

Large floors with many visible cubes sharing the same model should now spend less CPU time in repeated animated-model setup, especially after the earlier spatial-query and visibility filters have already narrowed the candidate set.
