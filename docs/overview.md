# Overview

## What This Project Is

`dream` is an isometric, gothic, multiplayer action RPG for Windows, written in C# on .NET 10 (OpenTK renderer, BulletSharp physics, SQLite persistence). A player picks a character, walks the world by clicking, fights enemies with skills, loots them, trades with vendors and takes quests. Everything that matters is decided by a server that owns the world; the desktop client predicts its own player, draws the replicated state and sends commands.

## Runtime Flow

`Program.Main()` selects a process mode (`Program.TrySelectProcessMode`): no argument runs the desktop client (`Game`, an OpenTK `GameWindow`); `--server [--world <id|name>]` runs the dedicated server (`ServerBootstrap.RunServerMode()`); `--worlds`, `--characters`, `--accounts`, `--items` and `--db` are administration command lines; any other argument is rejected.

The desktop client opens on a menu (Single Player, Host, Join; characters are chosen per account) and creates `PlayScene` only after a connection succeeds. Single Player and Host start an integrated server on loopback; Join connects to a dedicated or hosted server over TCP (default port 5057). If a session ends (lost connection, 8 s server silence, rejected or timed-out join) the client returns to the menu with the reason.

### Server

- Fixed 64 Hz simulation (`SimulationClock.AuthoritativeTicksPerSecond`, driven by `ServerTickRunner`); actor snapshots every second tick, 32 Hz by default (`DREAM_SNAPSHOT_INTERVAL_TICKS`).
- `WorldSessionRuntime` owns the authoritative world: players, enemies, combat, loot, quests, vendors, respawn. `DedicatedServerHost` and `DedicatedServerService` route protocol messages into it and broadcast state.
- Persistence is SQLite in WAL mode with group-committed writes (`dream/Server/Persistence`). A world is one database file, `player-profiles.db`; accounts, characters, inventories, quests and persisted enemies live in it. `--worlds` creates and lists worlds, and migrations run from `dream/config/migrations`.
- Content is loaded from `dream/config/content/` through `ContentCatalog.LoadFromDirectory` and validated at startup; the server exits with code 8 on invalid content.

### Protocol

Typed client and server message families in versioned `TransportEnvelope`s (`TransportEnvelope.CurrentProtocolVersion` = 22), JSON payloads in length-prefixed frames over TCP; a peer on another protocol version is refused. Join and resync worlds travel as chunked transfers.

### Client

Each update, the client captures input into a `PlayerCommandFrame` for the server, predicts the local player in its `ClientWorldSession`, reconciles with the acknowledged server state, applies snapshots and interpolates remote players and enemies. Each frame, it draws the isometric world (terrain, props, actors with skinned models, effects, war mist, lighting by a shared lighting block), then the HUD and windows (inventory, character, vendor, dialogue, chat). Controls are isometric click-to-move: right mouse button walks, attacks or builds at the clicked cell, left mouse button aims and (in Combat mode) attacks, `WASD` moves relative to the facing; see the README for the full list.

## Main Systems

| Area | Where | Notes |
|---|---|---|
| Application | `dream/Application` | `Program`, `Game` (window shell and scene switching), `GameSettings` |
| Client | `dream/Client` | menu and startup scenes, `PlayScene`, input, navigation (click-to-move pathing), replication, audio, identity |
| Server | `dream/Server` | host, transport, accounts, characters, worlds, persistence, tick pacing, admin commands |
| Multiplayer | `dream/Multiplayer` | protocol envelopes, codecs, transport, message contracts |
| Simulation | `dream/Simulation` | world runtime, combat and stat rules, inventory, content catalog, enemies, players, deterministic helpers |
| Rendering | `dream/Rendering` | renderer core, actors, ground, props, sky, lighting, effects, mist, shadows, HUD and UI sprites |
| Assets | `dream/Assets` | `AssetPaths`: runtime asset resolution from the output folder |
| Tests | `dream.Tests` | mirrors the production folders, plus `Integration`, `Load`, `BugHunt` and `ProjectStructure` (dependency rules) |

## Content And Art

- `dream/config/content/`: `items.json`, `enemies.json`, `loot.json`, `npcs.json`, `quests.json`, `skills.json`, `spawns.json` (the three default skills are Cleave, Bolt and Mend; enemies include Wolf, DireWolf, AshenProwler, GraveHowler and RisenSkeleton).
- `dream/shaders/`, `dream/animations/`, `dream/fonts/` (Cinzel and EB Garamond, SIL OFL, with their licences), `dream/ui/` (atlases and sprite tables), `dream/audio/`: runtime assets copied into the output folder.
- Art is original and procedural. `tools/ui-assets/` generates the UI, items, terrain, iso props and actors and effects with fixed seeds (output is byte-deterministic); `tools/audio/` generates the sound pack. See `tools/ui-assets/README.md`.

## Tooling

`tools/dev/` holds repo hygiene tools: `test.ps1` (test tiers with durations), `worktrees.ps1` (worktree and build-output report), `gallery.py` (HTML gallery of art and captures). `skills/capturing-dream-screenshots` captures the real game through the `DREAM_HUD_CAPTURE` harness. See [build-and-run.md](build-and-run.md).
