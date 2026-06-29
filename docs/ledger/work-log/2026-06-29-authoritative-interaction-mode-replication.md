# Authoritative Interaction Mode Replication

Purpose: record the next remote-mode authority step so build/combat mode state converges on the server snapshot path instead of staying client-local.
Status: complete
Scope: snapshot contract update, remote client mode correction, focused protocol/state tests, and documentation alignment

## Summary

Remote desktop mode now treats interaction mode as authoritative snapshot state too.

- Extended `ServerSnapshotEnvelope` with the current authoritative `PlayerInteractionMode`.
- Updated `WorldSessionRuntime.ExportServerSnapshotEnvelope(...)` so dedicated-server and local-transition snapshots both publish that mode explicitly.
- Updated `ClientCorrectionApplier.ApplySnapshotToRuntime(...)` so remote mode now applies authoritative interaction mode before searching for the local player actor snapshot.
- Updated snapshot construction call sites and focused protocol tests so the snapshot contract stays explicit and round-trippable.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ProtocolFoundationTests|ClientAuthoritativeStateApplierTests" /p:UseSharedCompilation=false`
  - Result: passed, `12` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `232` tests

## Remaining Gaps

- Remote mode still relies on snapshot correction rather than full prediction/rollback for local action flow.
- Interaction mode now converges authoritatively, but the shared runtime still only supports one remote-controlled joined player actor.
- Combat feedback and richer authoritative action confirmation still need later cleanup.
