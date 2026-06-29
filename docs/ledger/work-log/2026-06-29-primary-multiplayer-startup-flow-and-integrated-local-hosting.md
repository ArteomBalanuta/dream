# Primary Multiplayer Startup Flow And Integrated Local Hosting

Purpose: record the next multiplayer productization slice that makes multiplayer connection the primary desktop boot flow and starts one integrated local authoritative server automatically.
Status: complete
Scope: boot-time connection scene, startup UI renderer, integrated loopback server handle, connection coordinator, dynamic local port binding, and documentation alignment

## Summary

Desktop startup no longer assumes "enter gameplay first, think about networking later."

- Added `StartupConnectionScene` as the new primary desktop boot scene.
- Added `StartupMenuRenderer` as a startup-only ImGui path for host, port, and player-name entry plus `Use Local Server` and `Connect` actions.
- Added `ClientConnectionOptions` as the validated host/port/player-name input seam for the new boot flow.
- Added `NetworkSessionCoordinator` plus `IRemoteSessionConnector` so startup connection state and remote session creation stay out of both `Game` and `PlayScene`.
- Added `ILocalServerHandle` plus `IntegratedLocalServerHandle` so desktop mode now starts one integrated loopback authoritative server automatically and exposes its bound port to the startup UI.
- Updated `TcpServerTransport` and `DedicatedServerService` so local hosting can bind port `0`, expose the actual bound port, and start cleanly before the background run loop begins.
- Updated `Game` so desktop mode now:
  - starts the integrated local server
  - opens the startup connection scene
  - preserves env-driven values as a developer prefill path
  - only constructs `PlayScene` after a successful multiplayer connection

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ClientConnectionOptionsTests|NetworkSessionCoordinatorTests|TcpServerTransportTests" /p:UseSharedCompilation=false`
  - Result: passed, `7` tests

## Remaining Gaps

- The integrated local server is now a real first-class desktop lifecycle seam, but the startup UI does not yet expose advanced options such as explicit "host public server" configuration, disconnect/reconnect flow, or richer diagnostics.
- The larger simulation-side asymmetry still remains: the first joined remote session owns the full authoritative `Player` path, while additional joined sessions are still lighter-weight runtime actors.
