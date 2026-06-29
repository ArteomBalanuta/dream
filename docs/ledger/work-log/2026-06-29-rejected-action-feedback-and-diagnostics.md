# Rejected Action Feedback And Diagnostics

Purpose: record the next multiplayer client slice that turns rejected authoritative action events into visible HUD feedback and bounded action-flow diagnostics.
Status: complete
Scope: rejection-message presentation, authoritative action-flow tracing, snapshot/action diagnostics recording, focused tests, and documentation alignment

## Summary

The remote-client path now has a first clean diagnostics seam around authoritative action intake instead of only raw correction/event plumbing.

- Added `AuthoritativeActionFeedbackSystem` so `AuthoritativeActionRejectedEvent` can surface a short-lived HUD-visible rejection message.
- Extended `EventTraceSystem` so rejected authoritative action events now appear in the bounded scene-local event trace alongside combat and world-mutation facts.
- Added `AuthoritativeFlowTrace`, `AuthoritativeFlowTraceEntry`, `IMultiplayerDiagnosticsSink`, and `MultiplayerDiagnostics` as a small diagnostics layer that records:
  - session id
  - accepted command or action sequence where available
  - server tick
  - acceptance vs rejection
  - whether an immediate correction was applied
  - a compact action or snapshot detail string
- Updated `ClientAuthoritativeActionApplier` so authoritative action intake can optionally publish into that diagnostics seam without mixing diagnostics policy into runtime mutation logic.
- Updated `PlayScene` so remote snapshot and action intake now records diagnostics through the scene-owned sink while keeping the sink local and bounded.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter "MultiplayerDiagnosticsTests|ClientAuthoritativeStateApplierTests|AuthoritativeActionFeedbackSystemTests|EventTraceSystemTests" /p:UseSharedCompilation=false`
  - Result: passed, `19` tests

## Remaining Gaps

- The new diagnostics trace is scene-owned and test-covered, but it is not yet rendered in a dedicated in-game multiplayer diagnostics panel.
- Rejected-action HUD feedback is intentionally lightweight text only; richer styling, persistence rules, or action-history UX can come later if client prediction becomes broader.
