# Client Action Sequence Tracker

Purpose: Record the fix for false stale-action rejects when HUD actions follow combat/build command actions.

Status: Completed on 2026-07-04.

Scope: Client action sequencing, server authoritative action replay protection, inventory equip/stat update requests.

## Root Cause

The server accepts player actions with a per-session monotonic action sequence. Primary-use actions generated from command frames use the command frame sequence, while HUD actions such as inventory equip and stat updates used a separate client-local counter that started at `1`.

After combat or leveling, the server could already have accepted a command-derived action with a much higher sequence. The next weapon equip then arrived with a low HUD action sequence and was correctly rejected by the server as stale, even though the player intended a fresh action.

## Implementation

- Added `ClientActionSequenceTracker` in the client networking layer.
- The tracker observes local command frame sequences so explicit HUD actions advance past command-derived primary-use actions.
- The tracker also observes server action results so a client reconnect/session echo cannot move explicit action numbering backward.
- `PlayScene` now sends inventory equip and stat-update actions through this shared tracker instead of an independent HUD-only counter.

## Guardrail

Do not weaken server stale-action checks to fix this class of issue. The server rejection is the replay/idempotency protection doing its job. Client-originated action sources must stay in one accepted sequence domain per session.

## Verification

- `dotnet test .\dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~ClientActionSequenceTrackerTests" --nologo /p:UseSharedCompilation=false`
- `dotnet test .\dream.Tests\dream.Tests.csproj --filter "FullyQualifiedName~ClientActionSequenceTrackerTests|FullyQualifiedName~AuthoritativeActionFlowTests|FullyQualifiedName~RuntimeAuthoritativeSessionHostTests" --nologo /p:UseSharedCompilation=false`
