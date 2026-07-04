Purpose: Record the server-authoritative experience and HUD progression pass.
Status: Completed
Scope: Player-profile XP persistence, DB-backed progression formulas, authoritative enemy kill rewards, and HUD progression display.

## Completed

- added persistent `experience` to player profiles
- added DB-backed progression formula keys:
  - `player.experience_to_next_level`
  - `enemy.experience_reward`
- extended the server formula service to evaluate progression thresholds and enemy XP rewards
- added a small schema-upgrade guard so existing local databases gain the new `experience` column
- threaded `Level` and `Experience` through `PlayerCombatant`
- awarded XP only on authoritative fatal enemy hits
- supported multi-level progression in one reward grant
- exported player progression through actor snapshots for multiplayer correction
- surfaced `LVL`, XP percent, and raw XP progress in the gameplay HUD

## Verification

- `dotnet test dream.Tests\\dream.Tests.csproj --nologo /p:UseSharedCompilation=false`
- `dotnet build dream\\dream.csproj --nologo /p:UseSharedCompilation=false`

## Notes

- Current XP persistence stores progress within the current level, not lifetime accumulated XP.
- Thresholds and enemy rewards are now tunable from SQLite without recompiling the server.
