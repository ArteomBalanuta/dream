# Work log: /dev help, /dev tod and the open chat panel

- Purpose: make `/dev help` readable (one command per line), size the chat for it while it is open, and add
  `/dev tod <time>` to move the world clock.
- Status: on `integrate/dev-help`, not merged. Based on master `55a28f8c`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery`: contracts frozen first
  (`ServerWorldClockMessage`, `ChatHudState.MaxLinesPerMessage` / `OpenVisibleRows`), three implementers on
  disjoint files (server, wire, chat), integration, one review fix loop.

## What landed

- **Help.** `DevCommands.HelpLines` / `Help`: a header plus one line per command, `tod` included, derived from
  the real limits (`MaxSubcommands`, `MaxSpawnCount`, `MaxGiveCount`). Still ONE System `ServerChatMessage`
  per subcommand with '\n' between lines, so the capture harness's one-reply-per-subcommand count holds. An
  unknown subcommand is one line pointing at `/dev help`, never the whole list.
- **Chat.** System messages split on '\n' (at most 16 lines, a scan cap of 64 pieces); player channels never
  split. While the line is open the log is wider (~680 px at 1440x1024), shows up to 24 rows and word-wraps
  (surrogate-safe hard breaks); the typing box matches its width. Closed chat is pixel-identical to before.
  New capture step `chat:open|close`.
- **`/dev tod <time>`.** Jumps the authoritative world clock forward (`WorldClock.JumpForward`,
  `WorldTimeOfDay.ForwardTicks`) and sends every joined connection a `server.world-clock` message, so
  protocol 22 -> 23. `ClientWorldClock.ApplyJump` / `ApplyJoin` re-anchor the sky and keep a jump that
  overtook a chunked join. `tod off` is an error: the clock keeps running. The `tod:` capture override
  still wins.

## Verification

- Full suite on `b2e58876`: 10600 passed, 0 failed, 47 skipped.
- Captures: `play,dev:help,chat:open` at 1440x1024 and 1280x720 (whole help visible, wrap at the body column);
  `dev:tod:night` turns the scene to night; `tod:12,dev:tod:night` stays noon.
- Golden: every `01-play.png` fails, all other frames pass. Not this branch: the baselines (`2ba6d816`)
  predate master's Combat default (`27c96b56`), so they still show Building mode and its build panel.
  Re-record them on master.

## Traps

- Clients only received the world clock at join. Any server-side clock move needs a re-anchor message,
  or every client's sky is wrong until it rejoins.
- `dev:tod:21:30` reaches the server as `tod 21 30` (the dev step turns colons into spaces), so the grammar
  joins two numeric tokens.
- Two full-suite runs aborted silently (testhost exit, no stack, no dump) while another session was
  capturing from the main checkout. Neither reproduced alone or in a dump-armed rerun; treat a silent abort
  as interference first, and arm `DOTNET_DbgEnableMiniDump` to tell a crash from a kill.
