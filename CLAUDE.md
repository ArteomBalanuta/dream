# Team workflow

For any non-trivial feature or refactor, coordinate the subagents in `.claude/agents/` like this:

1. **Plan** — Run the `architect`. It splits the work into 1–3 independent workstreams, each owning distinct files, plus a merge order.
2. **Build in parallel** — Launch one `implementer` per workstream, all at once (in the same turn) so they run in parallel. Give each one its workstream name, its owned files, and the shared contracts from the plan. Each runs in its own worktree and commits to its own branch.
3. **Integrate** — When all developers finish, run the `integrator` with their branch names and the merge order.
4. **Verify** — On the integration branch, run the `tester` and the `reviewer` (in parallel).
5. **Fix loop** — If the reviewer reports "Must fix" items or tests fail, send them back to an `implementer`, then re-run integrator → tester/reviewer. Stop after 2 loops and ask me.
6. **Hand off** — Summarize the integration branch, what changed, and test/review results. Do not push or merge into main; I do that.

For small changes (one or two files), skip the architect and use a single implementer.

# What we can already do

`AGENTS.md` lists every capability agents have here (see "Capabilities At A Glance"): see the game with screenshots
(`capture.ps1`), stage the world (`/dev`, the `dev:` and `tod:` capture steps), golden-image regression checks
(`tools/golden`), test tiers (`tools/dev/test.ps1`), content validation (`--validate-content`), and the repo skills in
`skills/`: assets, effects, combat skills, terrain, screenshots, multiplayer profiling, persistence. It also lists the
rules learned the hard way. Read it before planning.

When briefing a subagent, name the skill(s) and tools it should use, and remind it: no `git stash`, one test suite at a
time, reset its worktree onto the integration branch first, and look at visual changes in a capture before calling
them done.
