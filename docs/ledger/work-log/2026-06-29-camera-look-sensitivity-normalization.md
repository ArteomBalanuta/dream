# Camera Look Sensitivity Normalization

Purpose: record the follow-up fix for the local camera detick change that made mouse look far too sensitive by changing the meaning of the existing sensitivity setting.
Status: complete
Scope: camera-look sensitivity normalization, focused regression test coverage, and verification notes

## Summary

The deticked camera path now preserves a sane interpretation of the existing mouse sensitivity setting.

- Confirmed the regression came from removing the old implicit frame-time scale from camera look without replacing the scale that historic sensitivity values had been tuned against.
- Added a focused camera regression test that locks in "small per-frame rotation" behavior for the default sensitivity setting.
- Updated `Camera.ApplyLookDelta(...)` to normalize raw mouse delta through a fixed `1/60f` scale instead of multiplying by live frame time.
- Kept the earlier detick fix intact, so the local camera remains frame-driven and no longer depends on the 64 Hz authoritative tick cadence for smoothness.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter CameraTests /p:UseSharedCompilation=false`
  - Result: failed first with `Actual: 0.598472178`, confirming the excessive rotation scale
  - Result after fix: passed
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ClientSimulationCommandPolicyTests|RemoteSessionPlayerStateTests|PlayerControllerTests" /p:UseSharedCompilation=false`
  - Result: passed, `10` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `253` tests

## Notes

- During intermediate verification, running multiple `dotnet test` commands in parallel against the same project caused temporary local build-output locking; sequential reruns resolved that without code changes.
