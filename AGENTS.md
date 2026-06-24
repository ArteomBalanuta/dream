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

- For C# project changes, run `dotnet build dream\dream.csproj`.
- For documentation-only changes, verify file paths, links, and referenced names.
- Report exactly what you verified.

## Ledger Policy

- Use `docs/ledger/analysis/` for investigations, audits, and exploratory notes.
- Use `docs/ledger/specs/` for approved design intent.
- Use `docs/ledger/plans/` for execution-ready plans.
- Use `docs/ledger/decisions/` for durable technical or process choices.
- Use `docs/ledger/work-log/` for dated records of completed work.
