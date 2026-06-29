# Client Local Camera Boundary And Noclip Default Restoration

Purpose: record the follow-up that keeps the local camera client-owned in remote multiplayer and restores noclip/fly speed to the intended default baseline.
Scope: local camera correction boundary, noclip default tuning, focused regression tests, and verification notes

## Summary

Two additional regressions were addressed:

- remote snapshot/correction intake still overwrote the local runtime player's facing, which meant authoritative server state could still steer the client camera
- the noclip/fly default had drifted to an overly large value and no longer matched the intended local movement feel

## Completed Work

- Updated `ClientCorrectionApplier.ApplySnapshotToRuntime(...)` so local runtime snapshot application now corrects:
  - position
  - health
  - interaction mode metadata
  and no longer overwrites the local camera-facing state.
- Added `ClientCorrectionApplier.ApplyCorrectionDeltaToRuntimePlayer(...)` and updated `ClientAuthoritativeActionApplier` so rejected-action correction no longer forces authoritative facing back onto the local camera.
- Restored the default noclip/fly speed baseline from `150f` to `18f` in `GameSettings`.
- Aligned the headless host's default player tuning with the same noclip baseline so server-authoritative noclip behavior does not keep the old inflated value.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ClientAuthoritativeStateApplierTests|GameSettingsTests|DedicatedServerHostTickTests" /p:UseSharedCompilation=false`

## Notes

- This does not remove gameplay-facing orientation from the multiplayer protocol. The server still receives look intent and still owns gameplay-facing state for authority and remote-player rendering.
- The change only enforces that the locally controlled desktop camera itself remains client-owned instead of being snapped by remote correction.
