# AGENTS.md

This file is for future Codex/LLM agents working in this repository. It defines how to work safely and effectively in `dream` without rediscovering the same repo context every session.

## Project Snapshot

- `dream` is a C# `.NET 8` desktop project.
- It uses OpenTK for windowing and OpenGL rendering.
- It uses BulletSharp for physics.
- It uses ImGui.NET for HUD/UI overlay rendering.
- Runtime assets include GLSL shaders, textures, fonts, and glTF model content.

## Required First Reads

- Read [README.md](README.md) first.
- Read the relevant files under [docs](/G:/workspace/projects/dream/docs) for the subsystem you are touching.
- Read [docs/ledger/README.md](/G:/workspace/projects/dream/docs/ledger/README.md) before creating or changing ledger records.
- Read relevant current files in [docs/ledger/specs](/G:/workspace/projects/dream/docs/ledger/specs), [docs/ledger/plans](/G:/workspace/projects/dream/docs/ledger/plans), [docs/ledger/decisions](/G:/workspace/projects/dream/docs/ledger/decisions), and [docs/ledger/work-log](/G:/workspace/projects/dream/docs/ledger/work-log) when the task overlaps prior work.

## Workflow

1. Understand the request.
2. Read the relevant project docs and source files.
3. Consult relevant ledger entries.
4. Make a small plan before editing when the task is non-trivial.
5. Implement with minimal unrelated change.
6. Verify what changed.
7. Update docs and ledger records when the task changes project knowledge or workflow history.

## Team Workflow

Non-trivial features and refactors run as a small team with five roles. Claude Code defines them as subagents in `.claude/agents/` and starts them from `CLAUDE.md`; other agents follow the same roles and hand-offs.

| Role | Does | Never |
| --- | --- | --- |
| `architect` | Explores the code and writes the plan: the goal, the files to change, 1–3 workstreams that each own distinct files, the contracts shared between them, a merge order, a test approach and open questions. | Edits files. |
| `implementer` | Builds one workstream in its own git worktree and branch, touching only that workstream's files. Builds and tests after each step, then commits on its own branch and reports branch, changes, commands and results. | Pushes, merges, rebases onto `master`, deletes branches, or weakens or deletes tests. |
| `integrator` | Creates `integrate/<feature>` from the base branch and merges the workstream branches in the planned order, building after each merge. Resolves conflicts by keeping both sides' intent and the shared contracts, then runs the full suite. | Pushes or merges into `master`. Guesses at a real design conflict; it reports one instead. |
| `tester` | Adds happy-path, edge-case and regression tests in the repo's xUnit style, runs them, and finds the root cause of failures. | Edits production code, or skips or loosens assertions. |
| `reviewer` | Reviews the diff for correctness, tests and maintainability, and reports **Must fix**, **Should fix** and **Nit** findings with `file:line`. | Edits code or invents issues. |

Flow:

1. **Plan:** the architect writes the plan. It goes in `docs/ledger/plans/YYYY-MM-DD-topic.md` when the work is substantial (see Ledger Policy).
2. **Build:** one implementer per workstream, all started together.
3. **Integrate:** the integrator merges the branches in the planned order.
4. **Verify:** the tester and reviewer run in parallel on the integration branch.
5. **Fix loop:** "Must fix" findings or failing tests go back to an implementer, then the integrate and verify steps run again. Stop after two loops and ask the user.
6. **Hand off:** summarize the integration branch, what changed, and the test and review results, and add the work-log entry. The user pushes and merges into `master`.

For small changes (one or two files), skip the architect and use a single implementer. Workstreams must not share files; if one workstream needs another's file, the implementer stops and reports it.

## Repo-Specific Constraints

- Some shader and model assets are referenced through hardcoded absolute Windows paths in the current source.
- `dotnet build dream\dream.csproj` currently succeeds with warnings; do not describe the build as warning-free.
- Documentation and ledger updates are part of the work when they are needed to keep project knowledge accurate.
- Do not silently rewrite historical ledger records unless the task explicitly requires it.

## Editing Rules

- Follow existing repo patterns in code and docs.
- Avoid unrelated refactors.
- Keep documentation aligned with meaningful project changes.
- Prefer factual updates over aspirational rewrites.
- Preserve process history when editing specs, plans, decisions, or work logs.

## Verification

- For C# project changes, run `dotnet build dream\dream.csproj` and `dotnet test dream.Tests\dream.Tests.csproj`.
- From Git Bash, prefix those commands with `MSYS_NO_PATHCONV=1` so `/p:` switches are not rewritten as paths.
- A running `dream.exe` locks `dream\bin` and fails the build's copy step. Do not kill a user's game process. Build and test with `/p:BaseOutputPath=artifacts/verify-bin/` instead. The output must stay inside the repo, because repo-relative tests find the root from their output folder.
- For multiplayer or performance changes, the harness in `skills/profiling-dream-multiplayer` runs a dedicated server plus desktop clients and writes `logs/perf-run-*`. Pass `-NoStopExisting` so it does not stop running `dream.exe` processes.
- For documentation-only changes, verify file paths, links, and referenced names.
- Report exactly what you verified.

## Ledger Policy

- Use `docs/ledger/analysis/` for investigations, audits, and exploratory notes.
- Use `docs/ledger/specs/` for approved design intent.
- Use `docs/ledger/plans/` for execution-ready plans.
- Use `docs/ledger/decisions/` for durable technical or process choices.
- Use `docs/ledger/work-log/` for dated records of completed work.
