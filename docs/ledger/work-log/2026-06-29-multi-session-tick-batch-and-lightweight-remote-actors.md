# Multi Session Tick Batch And Lightweight Remote Actors

Purpose: record the next multiplayer foundation step toward many joined clients sharing one authoritative server session.
Status: complete
Scope: batched per-session tick commands, additional joined-session actor snapshots, lightweight remote actor movement/look replication, and honest limitations

## Summary

The server/runtime path now has its first real multi-session foundation beyond "many sockets, one meaningful player."

- Changed the headless tick contract so `DedicatedServerHost` drains the latest pending command for every joined session each tick instead of only one primary command.
- Updated `ITickDrivenAuthoritativeSessionHost`, `IPlayerCommandInbox`, and `LatestPlayerCommandInbox` so batched per-session command frames flow through the host boundary cleanly.
- Added `RemoteSessionPlayerState` as a lightweight authoritative actor state for additional joined remote sessions.
- Updated `WorldSessionRuntime.AttachRemoteSession(...)` so:
  - the first remote session still maps to the full authoritative `Player` actor
  - additional joined sessions now receive distinct actor ids plus lightweight remote-session actor state
- Updated `WorldSessionRuntime.ExportServerSnapshotEnvelope(...)` so those lightweight joined actors now appear in player snapshots with their own session ids.
- Updated `RuntimeAuthoritativeSessionHost.AdvanceTick(...)` so additional joined-session commands can now drive those lightweight remote-session actors during the same authoritative tick window.
- Kept authoritative build/combat ownership honest by rejecting action requests from lightweight remote-session actors until the runtime supports true multi-actor world interaction.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "RemoteSessionPlayerStateTests|DedicatedServerHostTickTests|LatestPlayerCommandInboxTests" /p:UseSharedCompilation=false`
  - Result: passed, `5` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `235` tests

## Remaining Gaps

- Only the primary controlled player actor owns full authoritative Bullet/build/combat/world interaction.
- Additional joined sessions currently replicate movement/look through lightweight kinematic actor state only.
- Per-player interaction mode, combat state, and authoritative world mutation ownership still need a true multi-actor runtime phase.
