# Headless Command Intake And Tick Foundation

Purpose: record the first dedicated-server command-buffer and fixed-tick runtime-advance slice for multiplayer foundations.
Status: complete
Scope: server-side command intake, host tick advancement, runtime no-fresh-command behavior, and headless bootstrap verification

## Summary

The dedicated-server path now has a real command-to-tick foundation instead of only protocol routing.

- `PlayerCommandHandler` now forwards inbound `PlayerCommandFrame` payloads into a focused `IPlayerCommandInbox` seam instead of only remembering the last message on the routing context.
- `DedicatedServerHost` now owns a `LatestPlayerCommandInbox`, a `ServerTickRunner`, and an `AdvanceTime(...)` entry point that drains the current primary-session command into the fixed 64 Hz authoritative host loop.
- `ITickDrivenAuthoritativeSessionHost` now separates "can join/export/process actions" from "can be stepped by the dedicated host", which keeps host tick tests low-harness and avoids forcing protocol tests to construct the full Bullet/OpenGL runtime.
- `WorldSessionRuntime` now keeps the last accepted command frame and can continue advancing simulation on authoritative ticks even when no fresh command arrived for that tick.
- `RuntimeAuthoritativeSessionHost.CreateDefault()` now builds the runtime with `loadVisualAssets: false`, so `--server` no longer tries to construct OpenGL-backed world assets during headless bootstrap.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "DedicatedServerHostTickTests|JoinRequestHandler_WhenAuthorityAcceptsJoin_ReturnsJoinAcceptedMessage|PlayerActionHandler_WhenAuthorityIsAttachedAndSessionJoined_ReturnsAcceptedServerActionMessage|AuthoritativeActionFlowTests" /p:UseSharedCompilation=false`
  - Result: passed, `7` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning and existing project warnings
- Ran `dotnet run --project dream\dream.csproj --no-build -- --server`
  - Result: printed the reserved-server bootstrap message and exited intentionally because the long-running transport accept loop is still not implemented

## Remaining Gaps

- The authoritative host still supports only one remote-controlled player session attached to the current single-player runtime actor.
- Headless server bootstrap still does not own the long-running TCP accept loop or snapshot broadcast loop.
- Remote movement/build/combat still need the next end-to-end transport phase to connect real network I/O to `AdvanceTime(...)` and outbound snapshot publishing.
