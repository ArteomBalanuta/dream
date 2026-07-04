# Static Chunk Worker Build And Render-Thread Upload

Purpose: Move heavy static chunk CPU mesh generation off the render thread while keeping OpenGL uploads render-thread-owned.
Status: Completed
Scope: Static chunk renderer scheduling, worker mesh builds, staged upload application, verification.

## Goal

Reduce first-visibility and first-join frame stalls by splitting the static chunk pipeline into:

- worker-thread CPU mesh generation
- render-thread-only GL buffer upload

## Implementation Outline

1. Keep chunk extract/signature/plan logic on the render thread for this first pass.
2. Convert `StaticWorldChunkBuildInput` into worker-built payloads that contain ready CPU mesh data plus signatures.
3. Add one active worker build plus one latest queued request so repeated world changes do not upload stale meshes unnecessarily.
4. Apply completed worker results on the render thread with a small per-frame upload budget to avoid one-frame GL upload spikes.
5. Preserve old chunk buffers until replacement upload completes, then remove stale dirty-chunk buffers at the end of the upload batch.
6. Verify with focused renderer tests plus build/test commands.

## Result

- Implemented worker-thread CPU mesh builds through `StaticWorldChunkBuildRequest`, `StaticWorldChunkBuildBatch`, and `StaticWorldChunkBuildOutput`.
- Kept OpenGL buffer creation and replacement on the render thread with a staged per-frame upload budget.
- Preserved existing chunk buffers until replacement upload completed so visibility stayed stable during rebuild.
- Verified with focused static chunk renderer tests, full project build, and a two-client profiling harness run.
- Profiling shows the old catastrophic synchronous static sync stalls are reduced substantially, but first-visibility render-thread planning and early world draw spikes still remain as the next bottleneck.
