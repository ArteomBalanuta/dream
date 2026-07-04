---
name: profiling-dream-multiplayer
description: Launches and profiles the Dream multiplayer runtime with a dedicated server and one or more desktop clients. Use when investigating FPS drops, render stalls, client/server sync, invisible players, hotpaths, bottlenecks, server tick timing, network diagnostics, or when the user asks Codex to run clients and observe performance metrics.
---

# Profiling Dream Multiplayer

## Quick Start

Run the bundled harness from the repo root:

```powershell
powershell -ExecutionPolicy Bypass -File .\skills\profiling-dream-multiplayer\scripts\launch-and-summarize.ps1 -DurationSeconds 30 -ClientCount 2
```

Use `-SkipBuild` only after a successful build in the same checkout. Use `-KeepRunning` when manual visual inspection is needed after metrics are collected.

## Workflow

1. Read `README.md`, `docs/configuration.md`, and `docs/performance-and-optimizations.md`.
2. Stop stale `dream.exe` processes before building unless intentionally attaching to an active run.
3. Launch with `DREAM_AUTO_CONNECT=1`, `DREAM_RENDER_PERF_LOG=1`, and `DREAM_NET_DIAGNOSTICS=1`.
4. Capture one dedicated server and at least two clients when diagnosing multiplayer visibility.
5. Inspect both directions:
   - Server snapshots should show all joined players.
   - Each client should apply snapshots with the full roster.
   - Each client should render every non-local alive remote player.
6. Attribute bottlenecks from evidence, not FPS alone:
   - Render hotpath: high render perf timings, chunk sync/draw spikes, visible mesh growth.
   - Physics hotpath: high physics or collision sync timings.
   - Network/server hotpath: `server.tick.slow`, `server.broadcast.slow`, send latency, queued snapshots.
   - Client application hotpath: growing receive queues or slow command-send/apply logs.
7. Turn diagnostics off for normal FPS comparisons once the root cause is found.

## Log Reading

The harness writes to `logs/perf-run-*` and prints a summary. If deeper inspection is needed, search:

```powershell
Select-String -Path .\logs\perf-run-*\*.log -Pattern 'render-perf|client.render.remote|client.presenter.state|server.snapshot|slow|queued='
```

Key facts to report:

- exact log directory
- process/window FPS samples
- whether each client rendered remote players
- server player count and actor count
- top slow timings with categories
- likely bottleneck and next verification step

## Guardrails

- Do not claim a fix from one window title; compare logs from server and all clients.
- Do not judge render performance with collision debug enabled unless that debug overlay is the target.
- If `dotnet build` fails copying `dream.exe`, stop running game processes and rebuild.
- If the dedicated server port differs from the client endpoint, fix the launch setup before analyzing gameplay.
