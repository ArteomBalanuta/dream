# 2026-07-04 Static Chunk Worker Build And Render-Thread Upload

Summary: moved static chunk CPU mesh generation off the render thread, kept GL upload render-thread-owned, and verified the multiplayer first-visibility path with focused tests plus the local profiling harness.

## What Changed

- Added worker-build payload types so static chunk planning can hand CPU-only mesh jobs to a background task.
- Reworked `StaticWorldChunkRenderer` to track one active worker build, one queued replacement request, and one staged upload batch.
- Applied completed chunk mesh uploads on the render thread with a bounded per-frame upload budget instead of synchronously rebuilding every visible mesh in one frame.
- Added a renderer test that verifies worker build outputs preserve the planned mesh key and signature identity.

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~StaticWorldChunkRendererTests|FullyQualifiedName~StaticWorldChunkMeshBuilderTests"`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- `powershell -ExecutionPolicy Bypass -File .\\skills\\profiling-dream-multiplayer\\scripts\\launch-and-summarize.ps1 -DurationSeconds 20 -ClientCount 2 -SkipBuild`

## Observed Outcome

- The previous profiling pass had first-visibility static sync spikes in the hundreds of milliseconds and worst-case total frame stalls above one second.
- After this pass, steady-state frames settled back to low-single-digit milliseconds once chunk uploads finished.
- Initial visibility is still not cheap: render-thread `plan` plus early world draw bursts are now the primary remaining startup bottleneck, especially on the second client during first chunk visibility.
- The next optimization pass should move extract/plan work off the render thread or prewarm visible startup chunks before the first interactive frame.
