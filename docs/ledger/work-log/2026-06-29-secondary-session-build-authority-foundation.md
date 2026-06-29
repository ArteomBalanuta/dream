# Secondary Session Build Authority Foundation

Purpose: record the next multiplayer runtime step toward true multi-actor authoritative action ownership.
Status: complete
Scope: per-session authoritative action context resolution, secondary-session authoritative build acceptance, focused runtime tests, and test-harness support for Bullet-backed runtime tests

## Summary

The authoritative action path no longer assumes every one-shot gameplay action must be validated against the primary controlled player's global mode state.

- Added `AuthoritativeSessionActionContext` as the narrow runtime seam that resolves per-session action-validation facts before one-shot gameplay actions are processed.
- Updated `WorldSessionRuntime.ProcessAuthoritativeAction(...)` so:
  - unknown and unspawned sessions still reject cleanly
  - build actions validate against the acting session's authoritative interaction mode
  - secondary joined sessions can now place cubes authoritatively
  - secondary joined sessions still reject combat honestly because full multi-actor combat ownership has not landed yet
- Updated build/combat action handlers inside `WorldSessionRuntime` so mode checks can consume the resolved session-owned mode instead of always reading the one primary runtime field directly.
- Added focused runtime tests that lock in:
  - accepted authoritative cube placement for a secondary joined session
  - build validation based on session-owned mode instead of the primary session's current authoritative mode
- Updated the test project to copy Bullet native runtime files into the test output so runtime-backed multiplayer tests can run under `dotnet test`.
- Hardened `PhysicsIntegratorOwnershipTests` so xUnit no longer crashes while formatting fake Bullet body tokens during full-suite execution.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter AuthoritativeActionFlowTests /p:UseSharedCompilation=false`
  - Result: passed, `5` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with warnings, including the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `238` tests

## Remaining Gaps

- Secondary joined sessions still do not own full authoritative combat resolution.
- The primary Bullet-backed `Player` actor path is still special for movement/physics depth and camera-backed combat intent generation.
- A later phase still needs true per-player actor/runtime ownership for combat state, hit validation, and broader world interaction beyond cube placement.
