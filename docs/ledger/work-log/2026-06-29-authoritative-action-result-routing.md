# Authoritative Action Result Routing

Purpose: record the multiplayer roadmap slice that turned one-shot actions into explicit authoritative results.
Status: completed
Scope: `player.action` handling, runtime action authority seam, and explicit rejected/accepted action responses

## Summary

Completed work:

- added real server-side handling for `PlayerActionMessage` instead of storing and dropping the message
- made `PlayerActionHandler` always return a `ServerActionMessage`
- added explicit server-side rejection paths for:
  - action before join
  - session mismatch
  - missing authoritative runtime attachment
- added `WorldSessionRuntime.ProcessAuthoritativeAction(...)` as the current authority seam for one-shot build/combat processing
- updated the local authoritative runtime path so primary-use actions now flow through that same authority method instead of through a separate local-only branch

## Verification

Ran:

- `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter AuthoritativeActionFlowTests /p:UseSharedCompilation=false`

Results:

- authoritative action flow tests passed

Known limits:

- the default headless host still has no attached runtime, so remote action messages currently receive explicit rejection instead of a simulated world result
- full transport-fed accepted action results will require the later dedicated runtime host loop and real remote join flow
