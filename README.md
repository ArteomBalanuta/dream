# Dream

`dream` is an isometric, gothic, multiplayer action RPG for Windows and Linux (x64), written in C# on .NET 10. Windows is the tested platform; Linux bundles build and pass their file checks, but have not yet been started on a Linux machine (see [docs/cross-platform.md](docs/cross-platform.md)). You pick a character, walk a dark world seen from an isometric camera, fight enemies with skills, collect loot, trade with vendors and take quests. The world is owned by an authoritative server; the desktop client is a protocol client of it, whether the server is the one it starts on loopback for Single Player or a dedicated server elsewhere.

## At A Glance

- **Client:** OpenTK 4.9 window and OpenGL renderer, ImGui for menus, SharpGLTF for models, SixLabors.ImageSharp for images, NAudio (WinMM) on Windows and OpenAL elsewhere for the sound pack under `dream/audio/` (`DREAM_AUDIO_BACKEND` overrides). The camera is isometric; it is the only view the client reaches.
- **Server:** server-authoritative, fixed 64 Hz simulation (`SimulationClock.AuthoritativeTicksPerSecond`), actor snapshots every second tick (32 Hz by default, `DREAM_SNAPSHOT_INTERVAL_TICKS`). Physics through BulletSharp. TCP transport with length-prefixed JSON envelopes.
- **Protocol:** version 22 (`TransportEnvelope.CurrentProtocolVersion`); a peer on another version is refused.
- **Persistence:** SQLite worlds (`Microsoft.Data.Sqlite`, WAL mode). Accounts, characters, inventories, quests and enemies live in the world database `player-profiles.db`; schema and migrations are under `dream/config/`.
- **Content:** data-driven JSON in `dream/config/content/` (`items`, `enemies`, `loot`, `npcs`, `quests`, `skills`, `spawns`), validated at startup and by `dream.exe --validate-content`.
- **Art and audio:** procedural and original. Generators live in `tools/ui-assets/` (UI, items, terrain, actors, effects) and `tools/audio/` (sound pack); their outputs are committed under `dream/ui/`, `dream/animations/` and `dream/audio/`.

## Build, Run, Test

Prerequisites: the .NET 10 SDK (`global.json` pins the version) and Windows with OpenGL. Development, tests and the tooling are Windows-first; Linux is a packaging target.

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj                  # desktop client
dotnet run --project dream\dream.csproj -- --server      # dedicated server (TCP 5057, DREAM_PORT)
dotnet test dream.Tests\dream.Tests.csproj               # full suite, a few minutes
```

The executable is `dream\bin\Debug\net10.0\dream.exe`. Other process modes: `--worlds`, `--characters`, `--accounts`, `--items`, `--db` (administration command lines, see `Program.TrySelectProcessMode`) and `--validate-content`.

Faster loops (details in [docs/build-and-run.md](docs/build-and-run.md), Fast loops):

- `tools\dev\test.ps1 -Area Content|Rendering|Simulation|Net|Client|Fast|Full` runs a test tier with a timeout and logs durations.
- `dream.exe --validate-content` checks the content JSON without starting the game.
- `tools\dev\worktrees.ps1` reports (read-only by default) the git worktrees and their build output sizes.
- `tools\dev\gallery.py` builds an HTML page of the generated art and the newest capture screenshots.

## Shareable Builds

Self-contained client bundles that need no SDK or .NET runtime on the recipient's machine (details, Linux requirements and
what is verified: [docs/cross-platform.md](docs/cross-platform.md)):

```powershell
just package-windows    # artifacts\dist\dream-<version>-win-x64.zip
just package-linux      # artifacts\dist\dream-<version>-linux-x64.tar.gz (built on Windows too)
just package-all        # both, in parallel
just run-dist           # start the newest packaged client
```

Bundles are built from the last commit, not from the working tree. On Linux, unpack the tar.gz and run `./run-dream.sh`.

## Controls

Isometric click-to-move; `Esc` always backs out one layer (chat, then an open vendor, dialogue or trade window, then the inventory, then the pause menu).

- Right mouse button: walk to the clicked ground cell; on an enemy it sets an attack goal; in Building mode it places a cube.
- Left mouse button: the character faces the cursor, and in Combat mode holding the button attacks.
- `W` `A` `S` `D`: move relative to the facing. `Space`: jump.
- `B` / `G`: Building / Combat mode.
- `1` to `6`: the quickbar (skills Cleave, Bolt, Mend and the health and mana potions by default). Skills need Combat mode, except Mend.
- `Q` / `R`: drink the first healing / mana potion in the bag. `E`: pick up the nearest item or talk to the nearest NPC.
- `V`: inventory and character windows. `Enter`: chat. `Tab` (held): the online list. `Esc`: pause menu.
- Developer keys, only where the server allows dev commands (`DREAM_DEV_COMMANDS=1`, or your own Single Player server): `0` noclip and `F` freeze. `F3` swaps the HUD when `admin_hud=1` or `DREAM_ADMIN_HUD=1` is set.

## Documentation

- [Build and run](docs/build-and-run.md): building, running, tests, fast loops, runtime assets, troubleshooting
- [Overview](docs/overview.md): what the game is, runtime flow, main systems
- [Architecture](docs/architecture.md), [Project structure](docs/project-structure.md)
- [Configuration](docs/configuration.md): every `DREAM_*` environment variable and process exit code
- [Rendering and assets](docs/rendering-and-assets.md), [Performance and optimizations](docs/performance-and-optimizations.md)
- [Server persistence DDL](docs/server-persistence-ddl.md)
- `docs/ledger/`: plans, analyses, decisions and work records
- `skills/`: repo-local agent skills (capturing screenshots, generating assets, profiling multiplayer, persistence changes)
