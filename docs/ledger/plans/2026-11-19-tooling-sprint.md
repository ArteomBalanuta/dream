# Tooling sprint: run directories, dev staging, capture steps and verification tools

- **Purpose**: make the game fast to see and iterate on: parallel, reproducible captures; a staged world in one
  script step; shader and image checks that fail loudly; a content check that needs no window.
- **Status**: implemented on `tool/int`.
- **Scope**: tooling only. No content, gameplay or engine behaviour change; no protocol bump (stays 22); no schema
  change.

## Overview

1. **Run directories.** `DREAM_DATA_DIR` moves a run's writable state (database and its locks,
   `desktop-client.txt`, default identity) to a folder of its own; `DREAM_ASSET_ROOT` loads the read-only assets
   (shaders, animations, fonts, audio, ui, content, migrations) from another folder, normally the source tree, so
   edited JSON, atlases and shaders apply on the next launch with no rebuild. `DREAM_HUD_CAPTURE` implies
   auto-connect.
2. **Dev commands.** `/dev <sub>[; <sub>...]` stages the world from chat: `spawn`, `tp`, `give`, `clear`, `heal`,
   `hp`, `mana`, `help`. Carried as a `Command` chat line (no new message), server-authoritative, allowed only where
   dev commands are on **and** the connection is loopback, re-checked when each subcommand runs.
3. **Capture steps.** `dev:` sends a `/dev` line and waits for its replies; `tod:` sets the client-only visual time
   of day. `capture.ps1` gives each run a fresh data dir and a fixed seed, so captures run in parallel and repeat.
4. **Render verification.** Every GLSL program the product builds is compiled and linked in a hidden GL context,
   with coverage derived from the shader folder and two synthetic negatives. `tools/golden` compares capture PNGs
   against recorded baselines with a tolerant, noise-calibrated diff and writes diff PNGs.
5. **Content validator.** `dream --validate-content` runs the one content parser, the startup formula probe and
   missing-sprite warnings, without a server, lock, database or window.
6. **Hygiene.** `tools/dev/worktrees.ps1` (sizes, merged state, safe cleanup), `tools/dev/test.ps1` (test tiers
   with a watchdog and a duration log), `tools/dev/gallery.py` (an HTML index of art and captures), refreshed
   README, overview and build docs.

## Contracts

- **`DevCommandText`** (`dream/Multiplayer/Protocol/DevCommandText.cs`): the `/dev` prefix, reply and error
  prefixes, the `;` separator, at most 8 subcommands, and the one splitter client and server share.
- **Dev grammar.** One System reply per subcommand, `[dev] ` on success, `[dev] error: ` on failure; more than 8
  subcommands is one error and nothing runs; a failing subcommand does not stop the next. Enemy and item keys match
  case-insensitively. Off or remote: `[dev] error: Dev commands are off on this server.` The admin role is
  irrelevant.
- **Run directories.** Identity precedence `DREAM_IDENTITY_FILE` > `DREAM_DATA_DIR` > the per-user default. An
  invalid data dir throws naming the variable; an unusable asset root logs one line and uses the exe folder. The
  database never follows the asset root.
- **Capture steps.** `dev:a:b;c` becomes `/dev a b; c`; the step writes no PNG, waits for one reply per subcommand
  or 240 frames, then settles 30 frames. `tod:<hours>|off`, hours in [0,24). capture.ps1 exit 3 when a dev reply is
  an error or missing, unless `-AllowDevErrors`.
- **Validator exit codes.** 0 ok (warnings allowed), 1 usage, 8 invalid content, failed probe, or a warning under
  `--strict` (8 matches the server's content-invalid code).
- **Golden limit.** A scene fails when the fraction of differing pixels exceeds
  `max(maxFraction, 3 * selfDiffFraction)`, derived in code and pinned by a test.

## Design decisions

1. Resolvers take the environment as a function and are tested without touching process variables; the suite
   runs in parallel.
2. Staging is a chat command rather than a protocol message, so no version bump and no new wire surface; the gate
   is the conjunction of the server's dev setting and a loopback source, so a public host can never expose it.
3. `tod:` is client-only and reuses the Settings window's override; the replicated world clock is untouched.
4. Shader tests fail, not skip, when no GL context can be created, unless `DREAM_GL_TESTS=0` says so explicitly.
5. The validator lives in the application layer because the sprite checks need the UI sprite table, which the
   server may not reference.

## Out of scope

In-process golden rendering, live hot reload, a bot command line, `/dev time`, `/dev god`, `/dev level`, kills with
loot or XP.
