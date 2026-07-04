Purpose: Record the multiplayer diagnostics pass for player visibility and host-FPS investigation.
Status: Implemented
Scope: Network/server/client diagnostic logging, remote-player presentation/render breadcrumbs, and documentation.

## Summary

Added an opt-in multiplayer diagnostics stream controlled by `DREAM_NET_DIAGNOSTICS=1`. The logger writes to console and to `logs/network-diagnostics.log` under the application output folder unless `DREAM_NET_DIAGNOSTICS_PATH` overrides the destination.

The diagnostics are intentionally split across the player visibility pipeline:

- server transport accept, inbound protocol frames, outbound join/snapshot/action frames, and send timing
- authoritative join acceptance with primary session, registered sessions, roster, and join-time actor snapshots
- server tick snapshots with actor summaries plus advance/snapshot/broadcast timing
- remote client connect/send/receive events, including slow command-send warnings
- client scene application of join/snapshot/action messages
- `ClientScenePresenter` remote-entry, roster, sample, and prune facts
- remote-player renderer input/alive/visible/model-cache counts

This should make the current "players cannot see each other" bug traceable to a specific boundary: missing server actor, missing received snapshot, wrong local-session filtering, presenter prune, visibility filtering, or final model draw.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~NetworkDiagnosticsLogTests|FullyQualifiedName~ClientScenePresenterTests|FullyQualifiedName~RemotePlayerRendererTests|FullyQualifiedName~DedicatedServerServiceTests|FullyQualifiedName~RemoteClientSessionTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test .\dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build .\dream\dream.csproj --nologo /p:UseSharedCompilation=false`
