# Host Frame And Integrated Server Performance Counters

Purpose: Record the host-side instrumentation added for diagnosing low FPS when the integrated local server is active.
Status: Completed
Scope: Client frame slices, integrated server slices, HUD counters, render log output.

## Summary

Added host-visible performance instrumentation that separates render-frame cost from integrated-server cost.

The implementation now reports:

- total frame time
- `SwapBuffers` / present time
- enemy draw time
- local-player draw time
- remote-player draw time
- HUD/ImGui time from the previous frame sample
- integrated local-server tick time
- integrated local-server snapshot build time
- integrated local-server broadcast time

The integrated server now publishes its latest sampled performance through the local-server handle, and the desktop scene folds that into `FramePerformanceSnapshot` so both the HUD counters and `DREAM_RENDER_PERF_LOG=1` output can explain host-side bottlenecks more directly.

## Verification

- `dotnet test dream.Tests/dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~FramePerformanceSnapshotTests|FullyQualifiedName~DedicatedServerPerformanceSnapshotTests|FullyQualifiedName~DedicatedServerServiceTests|FullyQualifiedName~NetworkSessionCoordinatorTests"`
- `dotnet build dream/dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

HUD draw time and present time are reported from the most recent completed frame sample. That keeps the measurement honest because the current frame cannot display its own HUD/present cost until after those steps finish.
