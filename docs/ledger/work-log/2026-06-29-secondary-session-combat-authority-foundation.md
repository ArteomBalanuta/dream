# Secondary Session Combat Authority Foundation

Purpose: record the next multiplayer runtime step that extends per-session authoritative action ownership from build placement into first-pass combat.
Status: complete
Scope: lightweight remote-session combat state, per-session authoritative combat resolution, focused runtime tests, and documentation alignment

## Summary

Additional joined sessions no longer stop at authoritative build placement only.

- Extended `AuthoritativeSessionActionContext` so the resolved session context now also carries the acting session's `PlayerCombatant`.
- Updated `RemoteSessionPlayerState` so lightweight joined actors now own a first-pass `PlayerCombatant` and advance its cooldown state during authoritative ticks.
- Updated `WorldSessionRuntime.ProcessAuthoritativeAction(...)` and `ProcessCombatUseAction(...)` so accepted combat now resolves through the acting session's authoritative combat context instead of always using the primary player combatant.
- Enabled secondary joined sessions to resolve authoritative combat actions against destructible cubes while preserving the existing per-session interaction-mode checks.
- Normalized joined remote-session combat aim from authoritative actor position/facing so the server no longer trusts arbitrary client-provided combat rays for that path.
- Kept the slice honest: this is first-pass multi-session combat authority, not full multi-body parity with the primary Bullet-backed player path.

## Verification

- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo --filter AuthoritativeActionFlowTests /p:UseSharedCompilation=false`
  - Result: passed, `8` tests
- Ran `dotnet build dream\dream.csproj --nologo /p:UseSharedCompilation=false`
  - Result: build succeeded with warnings, including the known `BulletSharp` restore warning
- Ran `dotnet test dream.Tests\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
  - Result: passed, `238` tests

## Remaining Gaps

- Additional joined sessions still do not own a full Bullet-backed player body or camera stack.
- Enemy aggro, player-vs-player interaction, and broader player-owned combat presentation/correction still need later multiplayer phases.
- The action payload still carries client-provided combat ray data instead of deriving all hit context from a fully actor-owned authoritative aiming model.
- Primary-player combat still reuses the local runtime camera stack instead of a transport-agnostic actor-owned aiming model.
