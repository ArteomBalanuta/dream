# Client Authoritative Action Intake

Purpose: record the next multiplayer client slice that turns accepted server action results into immediate remote-mode runtime and feedback updates.
Status: complete
Scope: client-side authoritative action-result application, runtime actor resolution support, focused client applier tests, and documentation alignment

## Summary

The remote client no longer treats `ServerActionMessage` as passive protocol metadata only.

- Added `ClientAuthoritativeActionApplier` as the first focused seam for applying accepted authoritative action results on the client.
- Updated `PlayScene.ApplyReplicatedMessage(...)` so remote-mode `server.action` messages now flow into that applier immediately.
- Added runtime actor-resolution support through `WorldSessionRuntime.TryResolveCombatActor(...)`, plus targeted lookup helpers in `EnemyRegistry` and `WorldObjectRegistry`, so accepted combat results can resolve local feedback anchors by authoritative actor id.
- Accepted authoritative `WorldMutationResult` payloads can now update the local remote-mode world immediately through the existing mutation applier path.
- Accepted authoritative `CombatResolutionResult` payloads can now publish client-local `DamageAppliedEvent` feedback immediately when the target actor can be resolved locally.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ClientAuthoritativeStateApplierTests /p:UseSharedCompilation=false`
  - Result: passed, `6` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with warnings, including the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed

## Remaining Gaps

- Rejected authoritative action results still do not drive any explicit rollback UX beyond the existing snapshot path.
- Combat result payloads still do not carry enough presentation data to preserve crit-vs-regular styling or richer immediate feedback without inference.
- Client-side immediate world updates still rely on later snapshots as the final source of truth for convergence.
