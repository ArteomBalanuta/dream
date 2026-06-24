# Dream Ledger Design

**Date:** 2026-06-24

**Goal:** Create a neutral, project-owned documentation ledger under `docs/ledger/` for storing analysis, specs, plans, durable decisions, and work logs for ongoing LLM-assisted development.

## Why A Ledger

- It gives future LLM sessions a predictable place to read project thinking and execution history.
- It avoids tying project process documentation to a specific tool or plugin name.
- It separates transient analysis from approved designs, implementation plans, durable decisions, and completion records.

## Proposed Folder Structure

### `docs/ledger/README.md`

Purpose:
- Explain the ledger's role in the project
- Define folder purposes
- Document naming conventions
- Describe the expected workflow through the ledger

### `docs/ledger/analysis/`

Purpose:
- Store exploration artifacts such as codebase analysis, investigations, audits, and research notes

### `docs/ledger/specs/`

Purpose:
- Store approved design and specification documents before implementation

### `docs/ledger/plans/`

Purpose:
- Store execution-ready implementation plans

### `docs/ledger/decisions/`

Purpose:
- Store durable technical or process decisions that future work should respect

### `docs/ledger/work-log/`

Purpose:
- Store dated summaries of completed work and related references

## Workflow Model

The ledger should describe and support this normal flow:

- `analysis/` -> `specs/` -> `plans/` -> `work-log/`

`decisions/` may be written whenever a choice becomes durable enough that future work should reference it directly.

## Naming Convention

Files should use:

- `YYYY-MM-DD-topic.md`

This keeps the ledger chronological, easy to scan, and stable across many future tasks.

## Content Convention

Each ledger document should begin with a short metadata section that states:

- Purpose
- Status
- Scope

The exact wording can vary by file type, but the ledger README should document the expectation.

## Migration Plan

The current project already contains process artifacts under `docs/superpowers/`. These should be migrated into the new neutral ledger structure so the repository has one authoritative documentation workspace.

Expected migration targets:

- `docs/superpowers/specs/2026-06-24-documentation-design.md` -> `docs/ledger/specs/2026-06-24-documentation-design.md`
- `docs/superpowers/plans/2026-06-24-documentation-set.md` -> `docs/ledger/plans/2026-06-24-documentation-set.md`
- This design file should also become part of the ledger spec history

## README Updates

The top-level `README.md` should be updated so the repository refers to `docs/ledger/` as the home for planning and execution records instead of `docs/superpowers/`.

## Accepted Design Decisions

- Use `docs/ledger/` as the project-owned workspace for LLM collaboration artifacts
- Include `analysis`, `specs`, `plans`, `decisions`, and `work-log` subfolders
- Use an index file at `docs/ledger/README.md`
- Migrate existing process documents into the ledger
- Use chronological `YYYY-MM-DD-topic.md` filenames
