# Local Camera Look Deticked From Server Tick

Purpose: record the multiplayer follow-up that removes local first-person mouse look jitter introduced by the fixed 64 Hz authoritative tick path.
Status: complete
Scope: local camera look smoothing, fixed-tick look replay removal for the local client runtime, focused regression tests, and verification notes

## Summary

The local desktop camera no longer depends on the 64 Hz simulation cadence for first-person mouse look.

- Added `Camera.ApplyLookDelta(...)` so raw mouse deltas can be applied without extra `deltaTime` scaling.
- Updated the camera rig so local immediate-look and ticked look paths both use the same raw-delta rotation logic.
- Updated `PlayScene` so the local player's camera consumes mouse look once per frame before the fixed-step runtime advances.
- Updated `ClientSimulationCommandPolicy` so the local runtime no longer reapplies that same frame's look delta inside the fixed authoritative tick path.
- Kept movement, build/combat authority, and replicated facing ownership on the existing multiplayer/server seams instead of undoing the 64 Hz authoritative model.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "CameraTests|ClientSimulationCommandPolicyTests|RemoteSessionPlayerStateTests|PlayerControllerTests" /p:UseSharedCompilation=false`
  - Result: passed, `11` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `252` tests

## Notes

- A running `dream.exe` instance initially blocked rebuilds during verification; stopping the running game process resolved the lock as expected for this repo.
