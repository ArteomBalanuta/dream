# Remote Look Delta Accumulation And Floor Safe Presentation Smoothing

Purpose: record the multiplayer/input follow-up that fixes slow remote mouse aim snapping back and removes presentation-only floor dipping between authoritative ticks.
Scope: command inbox accumulation, floor-safe local presentation smoothing, focused regression coverage, and verification notes

## Summary

Two distinct bugs were traced to recent multiplayer/tick changes:

- slow mouse aim in remote multiplayer could be corrected back because the server inbox kept only the latest per-frame look delta before each 64 Hz tick and discarded the rest
- local presentation smoothing predicted full body velocity including negative Y, which could visually dip the camera below the floor between ticks

## Completed Work

- Updated `LatestPlayerCommandInbox` so per-session pending commands now merge instead of blindly overwriting:
  - look deltas accumulate across all frames before the next authoritative tick
  - one-shot inputs such as primary use, jump, freeze, and noclip toggle remain preserved until drain
  - the newest sequence and movement axes still win for steady-state movement intent
  - the latest non-null requested mode is retained
- Updated `Player.CreatePresentationCamera(...)` so local presentation smoothing predicts only horizontal X/Z motion between ticks and keeps authoritative vertical position unchanged.

## Verification

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "LatestPlayerCommandInboxTests|WorldSessionRuntimePresentationSmoothingTests|DedicatedServerHostTickTests" /p:UseSharedCompilation=false`

## Notes

- The remote look fix keeps local client immediacy and reduces snapshot/correction pullback during slow aim because authoritative server facing now receives the full summed look input for the tick window.
- The floor-safe smoothing fix is presentation-only and does not change authoritative physics or collision behavior.
