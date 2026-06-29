Purpose: record the multiplayer protocol update that makes client-edited player combat stats authoritative on the server and replicated back to the client.
Status: completed
Scope: player stat update protocol, authoritative runtime application, snapshot stat replication, client correction/apply paths, and verification notes

# Summary

Remote multiplayer now has an explicit authoritative sync path for player combat stats. Changing `Strength`, `Attack Speed`, or `Vitality` in the client settings UI no longer leaves the dedicated server on default stat values forever.

# Root Cause

- The settings menu already exposed live combat-stat editing through `HudInteractionRequest`.
- `PlayScene.ApplyHudRequest(...)` applied those stat changes directly to the local `PlayerCombatant`.
- The client/server protocol had no message or payload for player stat changes.
- `ActorSnapshot` did not carry authoritative player stat values, so snapshot correction could not fix drift after join or later resync.

# Changes

- Added `PlayerCombatStatsSnapshot` as a transport-safe combat-stat DTO.
- Added `PlayerStatsUpdateIntent` and `PlayerStatsUpdateResult` as typed authoritative action payloads.
- Extended payload polymorphism to support those new payloads and moved the JSON discriminator field to `$type` so it no longer collides with domain payload properties such as `WorldMutationResult.Kind`.
- Updated `PlayScene` and `RemoteClientSession` so remote clients send stat changes to the server through the authoritative action channel.
- Updated `WorldSessionRuntime.ProcessAuthoritativeAction(...)` so the server applies player stat updates to the session-owned `PlayerCombatant` and returns authoritative stat values in the result payload.
- Extended `ActorSnapshot` and server snapshot export so authoritative player combat stats are now included in player actor snapshots.
- Updated client authoritative apply paths so:
  - `server.action` stat-update results apply back into the local runtime immediately
  - authoritative player snapshots also reapply combat stats for join/resync correction

# Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false --filter "FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~RemoteClientSessionTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~ClientAuthoritativeStateApplierTests"`
- `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

# Notes

- The build and tests still report the known `NU1701` warning for `BulletSharp`.
- This pass fixes authoritative player combat stat ownership specifically. Other gameplay-affecting settings such as remote floor-size changes or remote noclip-speed tuning still need an explicit authoritative sync policy.
