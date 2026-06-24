# Ledger

The ledger is the project-owned workspace for analysis, planning, decisions, and execution history during LLM-assisted development.

## Purpose

Use the ledger to keep a durable record of:

- exploratory analysis
- approved designs and specs
- execution-ready plans
- durable technical or process decisions
- plain-language summaries of completed work

This structure gives future sessions a predictable place to read what has already been investigated, decided, planned, and done.

## Folders

### `analysis/`

Use this for exploratory material such as:

- codebase analysis
- investigations
- audits
- research notes

### `specs/`

Use this for approved designs and specifications that define what should be built before implementation starts.

### `plans/`

Use this for execution-ready implementation plans that break work into concrete tasks.

### `decisions/`

Use this for durable technical or process choices that future work should treat as established project guidance.

### `work-log/`

Use this for dated summaries of completed work, including links or references to the related specs and plans when useful.

## Naming Convention

Ledger files should use:

- `YYYY-MM-DD-topic.md`

This keeps the workspace chronological and easy to scan.

## Expected Workflow

The normal workflow is:

- `analysis/` -> `specs/` -> `plans/` -> `work-log/`

`decisions/` can be added whenever a choice becomes durable enough that future work should reference it directly.

## Document Conventions

Each ledger document should start with a short metadata section that makes the file easy to understand in isolation.

Recommended fields:

- Purpose
- Status
- Scope

The exact wording can vary by document type, but the goal is the same: a future reader should understand why the file exists and how authoritative it is.
