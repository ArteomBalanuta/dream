Purpose: Record the follow-up pass that made equipped tools affect authoritative damage-driving stats and server-side attack cadence.
Status: Completed
Scope: Base-vs-effective player stats, equipped-tool combat modifiers, server-authoritative attack speed, and matching regression coverage.

## Completed

- added authoritative tool stat modifiers to the server-side tool catalog
- split player combat state into persisted base stats plus recomputed effective stats
- kept `player_profiles` as the persistence home for base stats only
- recomputed effective damage-driving stats when:
  - player base stats change
  - equipped tool changes
  - persisted player state is restored
- made server snapshots and stat update results carry base stats for authoritative sync
- removed remote-client local stat pre-application before server confirmation
- updated local inventory application to mirror the currently equipped tool modifier for HUD/viewmodel responsiveness
- verified authoritative cooldown behavior through tests

## Verification

- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- This pass intentionally limits tool combat modifiers to damage-driving strength changes and attack-speed changes.
- Client-side feedback still renders instantly, but actual damage cadence remains determined by authoritative server ticks and server-side combat cooldowns.
