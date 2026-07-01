Purpose: plan the first startup-world pass that uses geometry subcells as authored shape material instead of only full cubes.
Status: completed
Scope: deterministic startup geometry, mini-cube render/collision export, multiplayer snapshot preservation, and default world size

## Goal

Make the startup world visibly use smaller cube pieces to form non-box silhouettes such as pyramids and rounded boulders while staying inside the current geometry-native render/collision pipeline.

## Approach

- Keep floor terrain and broad landmasses as ordinary full cells for performance.
- Add authored sculpted cells from `WorldGenerator` by seeding `GeometrySubdivisionState` during startup generation.
- Use a `6 x 6 x 6` sculpture subdivision resolution so landmarks read as intentional mini-cube voxel art without exploding draw counts as aggressively as higher resolutions.
- Grow the default startup world enough to place these shapes near the spawn approach while leaving open traversal space.
- Preserve authored startup subdivision during full world snapshot application so clients do not flatten sculpted terrain back into full cubes.

## Tasks

- Add failing tests for a larger default world size.
- Add generator/export tests proving startup geometry contains subcell-authored pyramid and rounded-form cells.
- Add replication tests proving join-time world snapshot application keeps authored subcell shapes as renderable mini-cubes.
- Implement deterministic shape helpers inside `WorldGenerator`.
- Let authored sculptures replace overlapping coarse raised cells so coarse terrain does not punch holes in the sculpture silhouettes.
- Add low filler clusters near the main shapes so the sculpture area reads as a dense authored zone instead of isolated objects in empty terrain.
- Fill strongly supported empty sculpture subcells as real geometry so obvious internal gaps become renderable and collidable mini-cubes.
- Keep a small render-only visual fill scale only as a seam polish for model-backed mini-cubes, without changing collision/debug sizes.
- Batch or instance geometry render blocks and merge static subcell collision bodies before submitting work to OpenGL or Bullet.
- Update docs and work log after verification.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`
