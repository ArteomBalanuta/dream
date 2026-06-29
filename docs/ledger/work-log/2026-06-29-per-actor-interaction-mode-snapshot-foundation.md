# Per Actor Interaction Mode Snapshot Foundation

Purpose: record the next multiplayer data-model step toward true per-player authoritative interaction ownership.
Status: complete
Scope: optional per-actor interaction mode in player snapshots, lightweight remote-session mode tracking, focused protocol/runtime tests, and documentation alignment

## Summary

The multiplayer snapshot model no longer assumes one player-mode fact is enough forever.

- Extended `ActorSnapshot` with an optional per-actor `InteractionMode`.
- Updated `WorldSessionRuntime.ExportServerSnapshotEnvelope(...)` so:
  - the primary authoritative player actor exports the runtime-owned interaction mode
  - lightweight remote-session actors export their own tracked requested mode
  - enemy actor snapshots keep `null` for this field
- Updated `RemoteSessionPlayerState` so command frames now also update the actor-owned requested interaction mode alongside movement/look state.
- Added focused protocol and runtime tests to lock in the new snapshot shape without changing enemy snapshot semantics.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "RemoteSessionPlayerStateTests|ProtocolFoundationTests" /p:UseSharedCompilation=false`
  - Result: passed, `10` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `237` tests

## Remaining Gaps

- The client does not yet consume per-actor mode for remote-player presentation or HUD decisions.
- Additional joined sessions still do not own full authoritative build/combat/world interaction.
- The runtime still needs a later true multi-actor phase to move action validation and mutation ownership beyond the primary controlled player actor.
