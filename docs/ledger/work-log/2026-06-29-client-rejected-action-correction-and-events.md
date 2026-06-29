# Client Rejected Action Correction And Events

Purpose: record the next multiplayer client slice that gives rejected authoritative action results an immediate correction and UX/event path.
Status: complete
Scope: rejected action-result correction application, surfaced rejection events, focused client applier tests, and documentation alignment

## Summary

Rejected authoritative action results no longer wait silently for later snapshot convergence.

- Added `AuthoritativeActionRejectedEvent` as the first explicit scene-facing payload for surfaced server action rejections.
- Extended `ClientCorrectionApplier` with direct `ClientCorrectionDelta` application so action-result corrections can reposition/reface the local replicated player immediately.
- Updated `ClientAuthoritativeActionApplier` so:
  - rejected results with `Correction` now apply that correction immediately to the local replicated player
  - rejected results with `Rejection` now publish `AuthoritativeActionRejectedEvent`
  - accepted-result behavior remains unchanged
- Added focused tests that lock in both immediate rejected correction application and rejection-event publication.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter ClientAuthoritativeStateApplierTests /p:UseSharedCompilation=false`
  - Result: passed, `8` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with warnings, including the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed

## Remaining Gaps

- No current HUD or diagnostics subscriber consumes `AuthoritativeActionRejectedEvent` yet.
- Correction deltas are still narrow and only cover player position/facing.
- Later phases still need richer rejected-action UX and stronger prediction bookkeeping if local prediction becomes more aggressive.
