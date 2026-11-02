# Multiplayer completion roadmap

Base: master 7264b34, protocol v13, schema v10. Findings come from a read-only code trace on 2026-11-02. No exploit was run, so B1 and B3 get a failing test first.

Working rules from the user: autonomous, swarm workflow, relevant tests only, speed over optimisation. No push or merge into master unless asked. Start each milestone from master on `integrate/mp-mN`. Parked and not re-proposed: pathing and wave I fix loop 5, HUD art findings, flaky tests, performance. Isometric belongs to `integrate/isometric`.

## Separation audit

Separation is sound. Inventory, equipment, stats, XP, HP and position are keyed per character on the server. Item actions cannot reach another player, and inventory data goes only to its owner. No static or default-id bleed was found. The holes are in authority.

| Id | Severity | Finding | Evidence |
|---|---|---|---|
| B1 | Security | Any client can toggle noclip and freeze | `Multiplayer/Protocol/PlayerCommandHandler.cs:33`, `Simulation/Players/PlayerMotor.cs:55-56` |
| B2 | Security | Any client can set base stats to the cap; only a client-side gate exists | `Multiplayer/Protocol/PlayerActionHandler.cs:75-82`, `Simulation/Runtime/WorldSessionRuntime.Commands.cs:319-340` |
| B3 | Bleed | Noclip and frozen state carry over to the next primary-seat occupant | `WorldSessionRuntime.Sessions.cs:102-107`, `PlayerMotor.cs:44-46` |
| B4 | Low | A new primary-seat character appears where the last occupant stood | `WorldSessionRuntime.Persistence.cs:101`, `WorldSessionRuntime.Sessions.cs:124,322-330` |
| B5 | Latent | Detach falls back to `Player.Combatant`, which could write the wrong stats | `WorldSessionRuntime.Sessions.cs:160-165` |
| B6 | DoS | A joined connection can repeat its join, forcing whole-world exports | `Multiplayer/Protocol/JoinRequestHandler.cs:64-82`, `Simulation/Runtime/RuntimeAuthoritativeSessionHost.cs:148-203` |
| B7 | DoS | No connection cap | `Server/DedicatedServerService.cs:236-246` |
| B8 | UX | A same-account takeover closes the old client with no reason | `Server/DedicatedServerHost.cs:958-960` |
| B9 | Low | The same character online in two worlds: last writer wins on stats | `Simulation/Persistence/RuntimePersistenceBridge.cs:381-382,566-580` |

Other gaps: `ix_player_inventory_slots_item_instance_id` is not unique, which must change before trade or ground items. Base stats and XP reach all clients in the shared snapshot. Last hit takes all rewards. Placed cubes have no owner check in combat.

## Decisions taken without asking, per the user's autonomy instruction

1. M0 shuts stat sliders, noclip and freeze off on dedicated servers now, ahead of stat points. They stay on for the integrated server and with `DREAM_DEV_COMMANDS=1`. This changes decision U4.
2. M2 balance: 5 stat points per level and XP curve `100 + level * 50`. Over-budget characters are not reset: their stats are kept and they earn no points until under budget. This avoids changing real characters.
3. Kill rewards: XP is shared among contributors, loot goes to the top damage dealer.
4. Build protection stays in M7.
5. Chat's input hook takes a one-line conflict with isometric rather than waiting for it.

## Milestones

Status: M0-M8 done, with M5, M6, M7 and M8 on `integrate/mp-m5` at protocol v18, schema v14. M9: first step done on `integrate/mp-m9`, at protocol v18, schema v14 (100 m default for new server worlds, tiered camps and quests, Ashen Prowler and Grave Howler). Follow-ups after isometric merges: the integrated server defaults to 100 (touches `Game.cs` and `GameSettings.cs`), the `docs/configuration.md` line 363 `--worlds create` default becomes 100 (it is 100 in the code now), visuals for the new tiers, depth read from `world.txt`, and stored authored terrain.

| # | Milestone | Size | Depends on |
|---|---|---|---|
| M0 | Authority and separation fixes | S-M | none |
| M1 | Chat, who list, character picker | M | M0 |
| M2 | Stat points, spawn camps, fair kills | M | M0 |
| M3 | Ground items, gold, vendors | L | M2 |
| M4 | Quickbar, consumables, mana, skills | L | M3 |
| M5 | Party and trade | M-L | M1, M3 |
| M6 | Quests | M | M3 |
| M7 | PvP and build protection | S-M | M5 |
| M8 | Admin and server ops | M | M1 |
| M9 | Zones and a bigger world | L | M2, M3 |

### M0: Authority and separation fixes
No protocol or schema change.

- **M0-A Dev command gate (B1, B2).** Add `bool DevCommandsAllowed => false;` as a default member of `IAuthoritativeSessionHost`, with a settable property on `RuntimeAuthoritativeSessionHost`. Bootstrap sets it from `DREAM_DEV_COMMANDS=1`, default off. The integrated server sets it on. When off, `PlayerCommandHandler` clears `FreezePressed` and `ToggleNoclipPressed`, and `PlayerActionHandler` rejects `PlayerStatsUpdateIntent`. `PlayerCommandOneShotInputFilter` gains `AllowDevToggles` so clients stop predicting noclip unless allowed. The client rule, set in `PlayScene` after the remote client is assigned: `onIntegrated = localServer is { IsReady: true } && LoopbackHost.IsLoopback(remoteClient.Options.Host) && remoteClient.Options.Port == localServer.BoundPort`; `AllowDevToggles = onIntegrated || DREAM_DEV_COMMANDS == "1"`. `localServer != null` is not enough (it is also passed for remote connections) and `admin_hud` plays no part.
- **M0-B Clean seat (B3, B4, B5).** Add `PlayerMotor.ResetSeatState`, called when a session vacates the primary seat. A primary seat with no world-state row spawns via `ResolveRespawnPosition`. Secondary lanes base on `_worldSpawn`. Replace the combatant fallback with skipping the save.
- **M0-C Connection hardening (B6, B7, B8).** A joined connection repeats a join at most once per 5 s. `MaxConnections` defaults to 64 (`DREAM_MAX_CONNECTIONS`). A forced takeover sends the old connection a reason first (closed on a tracked background task; the client shows it as its disconnect reason). Unjoined connections are closed after a 15 s join deadline; a client refused at the cap gets one "Server is full." JoinRejected.

Merge order B, A, C. Filter: `FullyQualifiedName~ClientInputValidationTests|FullyQualifiedName~InventoryHostileIntentTests|FullyQualifiedName~Wave7HeldUseAndStatsValidationTests|FullyQualifiedName~CharacterTakeoverTests|FullyQualifiedName~CharacterJoinTests|FullyQualifiedName~SessionRejoinIntegrationTests|FullyQualifiedName~Wave9SessionLifecycleLoopbackTests|FullyQualifiedName~DedicatedServerServiceTests|FullyQualifiedName~PlayerMotorTests|FullyQualifiedName~WorldSessionRuntimeTickEquivalenceTests|FullyQualifiedName~ConnectionHardeningTests|FullyQualifiedName~ServerBootstrapDevCommandsTests|FullyQualifiedName~SeatLifecycleGapTests`

### M1: Chat, who list, character picker
Protocol v14. A contract commit first adds `ChatChannel { World, Say, Whisper, System }`, `ClientChatMessage(Channel, Text, Target)`, `ServerChatMessage(Channel, From, Text, ServerTick)`, `ClientCharacterListRequest(AccountToken)`, `CharacterListEntry(DisplayName, Level)`, `ServerCharacterList(Characters)`.

- **M1-A Chat server.** `ChatMessageHandler`, `Server/ChatRouter.cs`. Joined only, 256 characters, control and bidi stripped, token bucket burst 5 refill 1/s, Say 30 m, sender name from the server, delivery by `PendingConnectionReply`. The list token is never queued, stored or logged.
- **M1-B Chat client.** `ChatPanel`, `ChatInput`, Enter to type, Esc to cancel, 8 lines, Tab online list from the actor cache. One line in `PlayScene.Update.cs` turns gameplay input off while typing.
- **M1-C Character picker.** After connect, list the account's characters, pick one or type a new name. The list reads through the persistence queue.

Merge order: contract, A, C, B. Filter: `FullyQualifiedName~Chat|FullyQualifiedName~CharacterLoopbackTests|FullyQualifiedName~CharacterJoinSqliteTests|FullyQualifiedName~ProtocolFoundationTests|FullyQualifiedName~ProtocolV2VersionRejectionLoopbackIntegrationTests`

### M2: Stat points, spawn camps, fair kills
Protocol v15, migration 0011.

- **M2-A Stat points.** `PlayerStatRules.PointsAvailable(level, stats)`, derived and not stored; `PlayerStatSpendIntent(Stat, Points)`. Strength and Vitality 1 per point, Attack Speed 1 per 0.05.
- **M2-B Spawn camps.** `config/content/spawns.json`, camps `{key, template, x, z, count, respawnSeconds, night?}`, respawn in ticks, `camp_key` on `enemy_instances`. No pathing changes. Re-pin goldens once, with the cause written down.
- **M2-C Fair kills.** Up to 8 contributors per enemy in a fixed array, per decision 3. Lower the XP curve only where the formula row has its default.

Merge order B, C, A. Filter: `FullyQualifiedName~EnemyLootEndToEndTests|FullyQualifiedName~LootGrantTests|FullyQualifiedName~EnemyFactoryTests|FullyQualifiedName~WolfSpawnPolicyTests|FullyQualifiedName~WaveT2NightSpawnTests|FullyQualifiedName~WolfRestartLockstepTests|FullyQualifiedName~SimulationStateChecksumTests|FullyQualifiedName~WorldSessionRuntimeTickEquivalenceTests|FullyQualifiedName~CombatFormula`

### M3: Ground items, gold, vendors
Status: done. Decision: gold is credited directly, there is no ground gold.

Unique index on `player_inventory_slots(world_id, item_instance_id)` after cleaning shared rows. Ground items use existing `state` and `world_position_*` columns, with a 30 s killer claim and 5 min despawn. A full bag drops to the ground. Pickup is range-checked and moves the item in one closure. `player_wallets(world_id, player_name, gold)`. `CreatureKind.Npc`, interact, buy and sell intents.

### M4: Quickbar, consumables, mana, skills
Status: done. Decisions: mana is per world; keys 1-6 are the quickbar, with the mode switches on B and G; potions do not stack; PvP is switched only by the `DREAM_PVP` env var. Sprites live separately on `integrate/iso-sprites`.

Keys 1-6, per-character quickbar rows, a use-item intent, server cooldowns in ticks, real mana, 3-4 skills. `PlayerController.cs` is an isometric file.

### M5: Party and trade
Status: done. Decisions: only the party leader can invite. `/accept` and `/decline` answer party invites only; trades are answered in the trade window. Trades conserve gold: both wallet changes sit in one closure with the item moves, debits first and credits last, so a failed write rolls back whole. A trade cancels on any change to either inventory fingerprint.

In-memory parties with invite, accept, leave, party chat and shared XP. Trade has offer, lock and confirm from both sides; inventories and gold change in one closure, and any change cancels it.

### M6: Quests
Status: done. Decisions: kill credit goes to every present contributor and their party mates within 40 m of the death; rewards and the completion share one closure.

`config/content/quests.json`, `player_quests`, NPC dialogue, HUD log, kill and collect objectives, rewards through the M3 grant path.

### M7: PvP and build protection
Status: done. PvP is switched by the `DREAM_PVP` env var only. Party cube access is off unless `DREAM_PARTY_CUBES=1`.

Per-world PvP flag. Only the owner, later the party, may damage a placed cube.

### M8: Admin and server ops
Status: done. Decisions: ban and mute are stored on the account and set by the in-game admin commands `/ban`, `/unban`, `/mute` and `/unmute`. The CLI is `--accounts grant-admin|revoke-admin|unban <name>`, and it has no ban or mute verb. The dedicated server keeps its 0.0.0.0 default bind, which `DREAM_BIND_ADDRESS` overrides; the integrated server stays on loopback.

Port, bind address, player cap, MOTD. Admin role, kick, ban, mute, announce. Client auto-retry. World-presence lease for B9. XP and base stats to the owner only.

### M9: Zones and a bigger world
First a larger authored stored world with camps by level. Portals between processes come later.
