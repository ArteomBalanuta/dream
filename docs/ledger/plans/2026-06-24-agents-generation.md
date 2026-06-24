# AGENTS.md Generation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate a valid root-level `AGENTS.md` for the `dream` repository that is concise, workflow-aware, and tailored for future Codex/LLM agents.

**Architecture:** The work will produce one root `AGENTS.md` informed by the current repository documentation and ledger. The file will act as an agent contract, combining project snapshot, first-read guidance, working workflow, repo-specific constraints, verification rules, and ledger usage policy without duplicating the full project docs.

**Tech Stack:** Markdown documentation, existing repository docs, ledger records, C#/.NET 8 project context

---

### Task 1: Gather the exact repository context for the file

**Files:**
- Read: `README.md`
- Read: `docs/overview.md`
- Read: `docs/architecture.md`
- Read: `docs/configuration.md`
- Read: `docs/ledger/README.md`
- Read: `docs/ledger/specs/2026-06-24-agents-design.md`
- Create: `AGENTS.md`

- [ ] **Step 1: Read the top-level project summary**

Read:

```text
README.md
```

Capture:

- current project purpose
- tech stack
- build command
- current runtime notes

- [ ] **Step 2: Read the technical project docs**

Read:

```text
docs/overview.md
docs/architecture.md
docs/configuration.md
```

Capture:

- subsystem names
- workflow-relevant constraints
- build and runtime assumptions

- [ ] **Step 3: Read the ledger instructions and AGENTS spec**

Read:

```text
docs/ledger/README.md
docs/ledger/specs/2026-06-24-agents-design.md
```

Capture:

- ledger folder roles
- naming and workflow model
- required sections and validity criteria for `AGENTS.md`

### Task 2: Draft the root `AGENTS.md`

**Files:**
- Create: `AGENTS.md`

- [ ] **Step 1: Write the file header and purpose**

Include a short opening that states:

- the file is for future agents
- it defines how to work safely and effectively in this repository

- [ ] **Step 2: Write the project snapshot section**

Include concise facts such as:

```markdown
## Project Snapshot

- `dream` is a C# `.NET 8` desktop project.
- It uses OpenTK for windowing and OpenGL rendering.
- It uses BulletSharp for physics.
- It uses ImGui.NET for HUD/UI overlay rendering.
- Runtime assets include GLSL shaders and glTF-based model content.
```

- [ ] **Step 3: Write the required first reads section**

Include repository-specific guidance such as:

```markdown
## Required First Reads

- Read `README.md` first.
- Read the relevant files under `docs/` for the subsystem you are touching.
- Read `docs/ledger/README.md` before changing project process docs or creating new ledger entries.
- Read the relevant current files in `docs/ledger/specs/`, `docs/ledger/plans/`, `docs/ledger/decisions/`, and `docs/ledger/work-log/` when the task overlaps prior work.
```

- [ ] **Step 4: Write the working workflow section**

Include a short ordered workflow such as:

```markdown
## Workflow

1. Understand the request.
2. Read the relevant project docs and source files.
3. Consult relevant ledger entries.
4. Make a small plan before editing when the task is non-trivial.
5. Implement with minimal unrelated change.
6. Verify what changed.
7. Update docs and ledger records when the task changes project knowledge or workflow history.
```

- [ ] **Step 5: Write the repo-specific constraints section**

Include concrete repo cautions such as:

```markdown
## Repo-Specific Constraints

- Some shader and model assets are referenced through hardcoded absolute Windows paths in the current source.
- `dotnet build dream\\dream.csproj` currently succeeds with warnings; do not describe the build as warning-free.
- Documentation and ledger updates are part of the work when they are needed to keep project knowledge accurate.
- Do not silently rewrite historical ledger records unless the task explicitly requires it.
```

- [ ] **Step 6: Write the editing, verification, and ledger sections**

Include concise guidance such as:

```markdown
## Editing Rules

- Follow existing repo patterns.
- Avoid unrelated refactors.
- Keep docs aligned with meaningful project changes.
- Prefer factual updates over aspirational rewrites.

## Verification

- For C# project changes, run `dotnet build dream\\dream.csproj`.
- For documentation-only changes, verify file paths, links, and referenced names.
- Report exactly what you verified.

## Ledger Policy

- Use `docs/ledger/analysis/` for investigations.
- Use `docs/ledger/specs/` for approved design intent.
- Use `docs/ledger/plans/` for execution-ready plans.
- Use `docs/ledger/decisions/` for durable choices.
- Use `docs/ledger/work-log/` for dated records of completed work.
```

### Task 3: Validate the draft against the design

**Files:**
- Test: `AGENTS.md`
- Test: `docs/ledger/specs/2026-06-24-agents-design.md`

- [ ] **Step 1: Check the file against the validity criteria**

Verify that `AGENTS.md`:

- exists at the repository root
- stays concise
- includes first-read guidance
- includes repo-specific cautions
- includes workflow guidance
- includes verification guidance
- includes ledger usage policy

- [ ] **Step 2: Remove duplication and filler**

Trim any text that merely repeats `README.md` or generic agent advice without helping on this repository.

### Task 4: Verify the repository state after adding the file

**Files:**
- Test: `AGENTS.md`
- Test: `README.md`
- Test: `docs/ledger/README.md`

- [ ] **Step 1: Verify the file paths and references**

Check that all referenced files and folders actually exist:

```powershell
Get-ChildItem AGENTS.md
Get-ChildItem docs
Get-ChildItem docs\ledger
```

Expected:

- `AGENTS.md` exists at repo root
- `docs/` exists
- `docs/ledger/` exists

- [ ] **Step 2: Run build verification if the drafting work touched project code**

Run only if code changed:

```powershell
dotnet build dream\dream.csproj
```

Expected:

- `Build succeeded.` with the current known warnings

- [ ] **Step 3: Record the work in the ledger when the file is created**

Create a `work-log` entry summarizing:

- that `AGENTS.md` was added
- what contract it established
- what was verified
