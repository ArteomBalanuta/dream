# Static Render Export Caching

Purpose: Stop rebuilding the whole world render export and re-hashing it on every client frame.
Status: Completed. Integrated on `integrate/engine-iteration` through `iteration/engine-base`.
Scope: `Map` presentation export, `GeometryRenderExport`, `StaticWorldChunkRenderer` export signature, tests.

## Why This Gap

Three specialist audits ran for this iteration: memory, graphics and threading, and netcode. Their findings are summarized in the work-log for this iteration. This plan takes the memory audit's top finding. It is the largest steady cost in the frame and sits on the seam between voxel meshing and rendering.

- `Map.CapturePresentationGeometryRenderExport` runs every frame on the client (`ClientWorldSession.CapturePresentationState`) and in the integrated runtime (`WorldSessionRuntime.CapturePresentationState`).
- Each call walks every solid cube and rebuilds `cells`, `blocks`, the compatibility cube lists and an `includedCells` set. It also allocates closures. On the startup world that is about 2 MB per frame, and the largest lists land on the large object heap.
- `StaticWorldChunkRenderer.CalculateExportSignature` then builds a `Dictionary<Cube, GeometryMaterialId>` the size of `Cells`, sorts the dirty cells with LINQ and hashes about 10k items. All of this only finds out whether static content changed. In steady state the answer is almost always no.
- Static content changes only through `MarkRenderDirty` for cells that are or were static, and through the merged static subcell block rebuild. The map already tracks this for `StaticDirtyCells`.

## Design

1. **Static content version.** `Map` gets `long StaticContentVersion`, taken from a process-wide `Interlocked` counter, so two maps never share a value and a rebuilt map never repeats an old one. It changes whenever static render content can change:
   - `MarkRenderDirty` when the cell is or was static;
   - `RebuildMergedStaticSubcellRenderBlocks`;
   - any other write to static cell records, static render blocks, `_staticRenderCells` or `_staticPhysicsCells` that does not go through `MarkRenderDirty`. The implementer audits every such write.
2. **Cached static portion.** `CapturePresentationGeometryRenderExport` keeps the radius-independent static part in a cache keyed by `StaticContentVersion`: static cells, static compatibility cubes and static blocks, including merged static subcell blocks. It rebuilds the cache only when the version changes. Each frame it builds only the dynamic part: dynamic cells, dynamic compatibility cubes, fracture blocks and rubble within the radius. It reuses the dynamic lists' backing storage where it is safe.
3. **Cells.** The export's `Cells` combines the cached static cells and the per-frame dynamic cells through a small read-only list wrapper, without copying. The cell order may change; consumers only map cubes to materials.
4. **Export contract.** `GeometryRenderExport` gets `long StaticContentVersion`, defaulting to `0`, which means unknown. Exports built by hand in tests keep `0`.
5. **Signature.** `CalculateExportSignature` hashes `StaticContentVersion` and the static dirty cells when the version is non-zero, and otherwise keeps today's full hash. Static dirty cells are hashed without LINQ sorting. An unchanged-export frame allocates nothing in the renderer.
6. **Empty frames.** Empty dirty arrays use `Array.Empty`. `Draw` uses a shared empty dirty-chunk set when the export is unchanged.
7. **Unchanged.** `CaptureGeometryRenderExport` and `CaptureFullGeometryRenderExport` keep their current behavior. So do the radius rules, the static/dynamic split and the main-thread GL ownership.

## Contracts

- The export's static content (`CompatibilityCubes`, `Blocks` and the static subset of `Cells`) is a pure function of `StaticContentVersion` for one `Map`.
- Consumers must not keep the export beyond the frame or mutate its lists. Today the extractor copies what it needs into arrays.
- Dirty cells are still drained exactly once per capture.

## Workstreams

One workstream (task A). The map export, the export record and the signature share one contract.

- Files: `dream/Simulation/World/Map.cs`, `dream/Simulation/World/Geometry/GeometryRenderExport.cs`, `dream/Rendering/World/StaticWorldChunkRenderer.cs`, the new `dream/Simulation/World/Geometry/ConcatenatedReadOnlyList.cs`, and new tests under `dream.Tests`.
- A short-lived draft split the iteration into five per-file workstreams. The user asked for a split by task, with more analysts, so the rest of the iteration now runs from `2026-10-27-engine-iteration-master-plan.md`, and this plan covers task A only.

## Tests

- Equivalence: after each kind of mutation (place, remove, push of a dynamic cube, ground cube changes, subdivision and merged subcells, fracture and rubble, static physics conversion), the cached presentation export holds the same static cubes, blocks and cell-to-material mapping as a fresh uncached capture.
- Version: it stays the same across frames with no static change or with only dynamic cube movement, and it changes after a static change. Two maps never share a version.
- Signature: equal for two unchanged frames, different after a static change, and the old path still works for exports with version `0`.
- Allocation: measure bytes allocated per steady-state frame (`CapturePresentationGeometryRenderExport` plus `CalculateExportSignature`) on the startup world with `GC.GetAllocatedBytesForCurrentThread`, before and after, and assert a bound well below today's cost.
- Full suite stays green.

## Risks

- A static change that skips the version bump would leave a stale static mesh. The equivalence tests cover each mutation path, and the audit of writes is part of the work.
- Static `Cube` objects are shared with the map. The cache stores references, as the export already does.
