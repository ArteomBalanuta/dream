# Local Presentation Smoothing Between Authoritative Ticks

Purpose: record the movement and camera smoothing follow-up after the 64 Hz authoritative tick migration made local desktop presentation feel visibly quantized.
Scope: root-cause analysis, presentation-only camera smoothing, focused regression tests, and verification notes

## Summary

The local desktop jitter came from render reading a player body/camera position that only changed on authoritative 64 Hz ticks.

Mouse yaw/pitch was already applied immediately per frame, but local body position still advanced only inside the fixed tick callback. At higher render frame rates that produced repeated identical camera positions between ticks followed by visible snaps on the next tick.

## Completed Work

- Exposed `ServerTickRunner.RemainingSeconds` so the desktop client can see the current sub-tick remainder without changing authoritative simulation.
- Added `Player.CreatePresentationCamera(...)` so the local client can build a presentation-only camera from:
  - the latest authoritative body transform
  - the current body linear velocity
  - the frame's remaining sub-tick lead time
- Updated `WorldSessionRuntime.CapturePresentationState(...)` to accept an optional presentation lead time and use the predicted presentation camera for render-facing state capture.
- Updated `PlayScene.Render(...)` to pass the tick runner remainder into presentation capture, which smooths local camera/movement rendering between authoritative ticks while keeping gameplay authority on the 64 Hz path.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "ServerTickRunnerTests|WorldSessionRuntimePresentationSmoothingTests|CameraTests" /p:UseSharedCompilation=false`

## Notes

- This change is presentation-only. It does not change authoritative tick cadence, action validation, or server ownership.
- The authoritative simulation still runs at 64 Hz.
- The fix specifically targets local desktop smoothness so tick rate no longer directly dictates visible camera/body motion cadence.
