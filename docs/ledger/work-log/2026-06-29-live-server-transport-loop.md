# Live Server Transport Loop

Purpose: record the first long-running dedicated-server transport loop and framed connection pump for multiplayer.
Status: complete
Scope: TCP accept loop, per-connection protocol pump, snapshot broadcast, and server-mode smoke verification

## Summary

The dedicated-server path is no longer only a bootstrap stub.

- Added `IServerTransport` so the live host loop depends on a narrow server-transport abstraction instead of directly on `TcpListener`.
- Added `ServerConnection` as a focused per-connection protocol pump that:
  - reads framed bytes from `ITransportConnection`
  - deframes and decodes client messages
  - routes them through `DedicatedServerHost`
  - serializes and frames server replies back onto the socket
- Added `DedicatedServerService` as the long-running host loop that:
  - starts the server transport
  - accepts clients
  - owns connection lifetime tasks
  - advances the authoritative host on the fixed tick loop
  - broadcasts snapshots only to joined connections
- Updated `ServerBootstrap` and `Program` so `--server` now starts the real long-running TCP server path instead of printing a placeholder and exiting.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "DedicatedServerServiceTests|ServerConnectionTests|DedicatedServerHostTickTests|JoinRequestHandler_WhenAuthorityAcceptsJoin_ReturnsJoinAcceptedMessage|PlayerActionHandler_WhenAuthorityIsAttachedAndSessionJoined_ReturnsAcceptedServerActionMessage|AuthoritativeActionFlowTests" /p:UseSharedCompilation=false`
  - Result: passed, `11` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning and existing project warnings
- Ran a live server smoke:
  - started `dotnet run --project dream\dream.csproj --no-build -- --server`
  - opened a loopback TCP client to `127.0.0.1:5057`
  - sent one framed `client.hello`
  - received a framed `server.hello` reply with protocol version `1`

## Remaining Gaps

- The authoritative runtime still supports only one remote-controlled joined player session.
- The desktop client is not yet attached to the remote transport edge for end-to-end movement/build/combat over the live TCP loop.
