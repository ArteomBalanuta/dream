# AGENTS.md Design

**Purpose:** Define what a valid, repo-specific `AGENTS.md` should contain for the `dream` project.

**Status:** Approved working design

**Scope:** Root-level `AGENTS.md` content, structure, and repository-specific expectations for future Codex/LLM agents

## Goal

Create a root `AGENTS.md` that acts as a balanced, workflow-aware collaboration contract for future Codex/LLM agents working in this repository.

The file should be:

- easy to find
- cheap to parse repeatedly
- specific to this repository
- strong enough to prevent common agent mistakes
- aligned with the project ledger model

## Primary Audience

The document should primarily optimize for:

- future Codex/LLM agents working inside this repository

It does not need to be written as a general contributor guide for humans first, although humans should still be able to read it clearly.

## Validity Criteria

A valid `AGENTS.md` for this repository should satisfy all of the following:

- It lives at the repository root as `AGENTS.md`
- It describes the current project accurately
- It tells agents what to read first before changing code or docs
- It includes repository-specific cautions rather than only generic best practices
- It defines a recommended order of operations for agent work
- It explains how and when to use the ledger under `docs/ledger/`
- It defines minimum verification expectations
- It avoids becoming a long handbook with unnecessary examples

## Recommended Structure

### 1. Purpose

A short opening that explains:

- this file is for future agents
- it defines how to work safely and effectively in this repository

### 2. Project Snapshot

A compact summary of the repo as it exists today, including:

- the project is a C# `.NET 8` desktop project
- OpenTK is used for windowing and OpenGL rendering
- BulletSharp is used for physics
- ImGui.NET is used for HUD/UI overlay work
- glTF assets and GLSL shaders are part of the runtime

This section should stay short and factual.

### 3. Required First Reads

This section should tell agents to read, as applicable:

- `README.md`
- relevant technical docs under `docs/`
- `docs/ledger/README.md`
- relevant current documents under `docs/ledger/specs/`, `docs/ledger/plans/`, `docs/ledger/decisions/`, and `docs/ledger/work-log/`

The instruction should emphasize relevance rather than forcing every ledger file to be read every time.

### 4. Working Workflow

This section should give agents a recommended sequence such as:

1. Understand the request
2. Read the relevant project docs and source files
3. Consult relevant ledger entries
4. Make a small plan before editing when the task is non-trivial
5. Implement while following existing repo patterns
6. Verify the result
7. Update docs and ledger records when the task changes project knowledge or workflow history

### 5. Repository-Specific Constraints

This section should capture the most important repo-specific cautions, including:

- the project currently has hardcoded absolute Windows paths for some shader/model assets
- `dotnet build dream\dream.csproj` currently succeeds with warnings rather than a fully clean build
- documentation changes and ledger updates are first-class work when they are needed for correctness or project memory
- historical ledger documents should not be silently rewritten unless the task specifically requires it

### 6. Editing Rules

This section should instruct agents to:

- follow existing code and documentation patterns
- avoid unrelated refactors
- keep docs aligned with meaningful project changes
- prefer factual updates over aspirational rewrites
- preserve repository history and intent when editing process docs

### 7. Verification

This section should define the minimum verification bar:

- when C# project code changes, run `dotnet build dream\dream.csproj`
- when documentation or ledger structure changes, verify links, paths, and referenced file names
- do not claim completion without reporting what was actually verified

### 8. Ledger Policy

This section should describe when to create or update:

- `analysis/` for exploration and investigations
- `specs/` for approved design-level intent
- `plans/` for execution-ready task breakdowns
- `decisions/` for durable technical or process choices
- `work-log/` for dated records of completed work

The policy should be practical and lightweight rather than procedural for its own sake.

## Tone And Style

The document should be:

- direct
- concise
- repo-specific
- operational rather than philosophical

It should avoid:

- long narrative explanation
- generic AI safety filler
- detailed examples unless they are necessary for a repo-specific caution

## Out Of Scope

The initial `AGENTS.md` does not need to:

- teach C# or OpenTK basics
- duplicate the full contents of `README.md` or `docs/`
- encode every possible future workflow
- replace the ledger itself

## Dependencies For Drafting

When generating the actual file, the author should use the current repository context from:

- `README.md`
- `docs/overview.md`
- `docs/architecture.md`
- `docs/configuration.md`
- `docs/ledger/README.md`
- current relevant ledger records

## Success Condition

The design is successful if a future agent can read `AGENTS.md` and quickly answer:

- what this repo is
- what to read first
- how to work here
- what to verify
- when to touch the ledger
