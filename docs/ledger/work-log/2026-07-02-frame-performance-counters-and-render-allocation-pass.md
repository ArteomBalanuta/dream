Purpose: Record the first implementation slice from the next performance optimization analysis.

Status: Completed

Scope: Frame diagnostics, HUD toggle, render-visible counters, physics/export timing, and low-risk instanced-render allocation cleanup.

## Summary

Implemented the first near-term performance slice from [2026-07-02-next-performance-optimization-options](../analysis/2026-07-02-next-performance-optimization-options.md).

The game now has an opt-in `Show Performance Counters` setting in the ImGui settings panel. When enabled, the HUD reports:

- render-export capture time
- physics step time
- exported and visible compatibility cube counts
- exported and visible geometry block counts
- compatibility and geometry batch counts
- current map collision body count

This gives future rendering and collision changes a live before/after signal without relying only on external probes.

## Render Allocation Changes

Added `ReusableMatrixBuffer` and switched the model-backed cube/block batch paths to reuse CPU-side matrix arrays instead of allocating a fresh `Matrix4[]` per batch.

`AnimatedModel.RenderPreparedInstanced(...)` now has an overload that accepts an existing matrix array plus an active count, and `UploadInstanceModelMatrices(...)` uploads that active range directly instead of calling `ToArray()` before `GL.BufferData(...)`.

The compatibility `IReadOnlyList<Matrix4>` overload remains available, but it copies into reusable source buffers and keeps node-composed instance matrices separate so multi-node glTF models do not overwrite their own source transforms.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo --filter "ReusableMatrixBufferTests|FramePerformanceSnapshotTests|HudInteractionRequestTests|GameSettingsTests|WorldSessionPresentationStateTests" /p:UseSharedCompilation=false`

Focused result at implementation time: `28` passed, `0` failed.
