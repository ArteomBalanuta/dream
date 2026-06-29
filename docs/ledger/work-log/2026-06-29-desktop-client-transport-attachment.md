# Desktop Client Transport Attachment

Purpose: record the first desktop-client connection to the live multiplayer transport edge.
Status: complete
Scope: env-driven remote client configuration, hello/join bootstrap, command submission, inbound authoritative message draining, and client-path limitations

## Summary

The desktop client can now opt into the live TCP transport path without collapsing render-thread code into socket logic.

- Added `IClientTransport` and `TcpClientConnector` so desktop connection setup depends on a small client-transport abstraction instead of directly on one static TCP helper.
- Added `RemoteClientOptions` so the desktop path can resolve remote transport settings from environment:
  - `DREAM_SERVER_HOST`
  - `DREAM_SERVER_PORT`
  - `DREAM_CLIENT_SESSION_ID`
  - `DREAM_CLIENT_NAME`
- Added `RemoteClientSession` so the desktop client can:
  - connect to the server
  - send `client.hello`
  - send `client.join-request`
  - submit `PlayerCommandFrame` messages
  - receive framed server messages on a background loop
  - expose queued authoritative messages for main-thread draining
- Updated `Game` to create a remote client session when `DREAM_SERVER_HOST` is configured.
- Updated `PlayScene` to:
  - drain remote server messages each update
  - apply them through the existing client protocol/presenter path
  - mirror local command frames onto the remote session when connected

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "RemoteClientSessionTests|DedicatedServerServiceTests|ServerConnectionTests|DedicatedServerHostTickTests|JoinRequestHandler_WhenAuthorityAcceptsJoin_ReturnsJoinAcceptedMessage|PlayerActionHandler_WhenAuthorityIsAttachedAndSessionJoined_ReturnsAcceptedServerActionMessage|AuthoritativeActionFlowTests" /p:UseSharedCompilation=false`
  - Result: passed, `14` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning and existing project warnings

## Remaining Gaps

- The desktop client still keeps its local runtime alive in remote mode for local presentation continuity, so this is not yet full authoritative local-player/world replacement.
- The first remote client path currently mirrors command frames only; stronger reconciliation, correction, and local authoritative state application still need a dedicated follow-up phase.
- The server still supports only one remote-controlled joined player session inside the authoritative runtime.
