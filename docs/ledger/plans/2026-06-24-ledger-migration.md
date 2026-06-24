# Dream Ledger Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a neutral `docs/ledger/` workspace for ongoing LLM-assisted development records and migrate the existing process artifacts into it.

**Architecture:** The repository will gain a new `docs/ledger/` index plus dedicated subfolders for analysis, specs, plans, decisions, and work logs. Existing design and plan files under `docs/superpowers/` will be moved into the matching ledger folders, and top-level documentation will be updated to reference the ledger instead of the tool-branded location.

**Tech Stack:** Markdown documentation, repository folder organization, existing project documentation files

---

### Task 1: Create the ledger structure

**Files:**
- Create: `docs/ledger/README.md`
- Create: `docs/ledger/analysis/.gitkeep`
- Create: `docs/ledger/specs/`
- Create: `docs/ledger/plans/`
- Create: `docs/ledger/decisions/.gitkeep`
- Create: `docs/ledger/work-log/.gitkeep`

- [ ] **Step 1: Create the `docs/ledger/` directory tree**

Create subfolders for:

- `analysis`
- `specs`
- `plans`
- `decisions`
- `work-log`

- [ ] **Step 2: Write `docs/ledger/README.md`**

Document:

- the purpose of the ledger
- what belongs in each folder
- the naming convention `YYYY-MM-DD-topic.md`
- the normal workflow `analysis -> specs -> plans -> work-log`
- the expectation that durable choices may also be captured in `decisions/`

### Task 2: Migrate existing process artifacts

**Files:**
- Move: `docs/superpowers/specs/2026-06-24-documentation-design.md` -> `docs/ledger/specs/2026-06-24-documentation-design.md`
- Move: `docs/superpowers/specs/2026-06-24-ledger-design.md` -> `docs/ledger/specs/2026-06-24-ledger-design.md`
- Move: `docs/superpowers/plans/2026-06-24-documentation-set.md` -> `docs/ledger/plans/2026-06-24-documentation-set.md`
- Move: `docs/superpowers/plans/2026-06-24-ledger-migration.md` -> `docs/ledger/plans/2026-06-24-ledger-migration.md`

- [ ] **Step 1: Move the existing spec files into `docs/ledger/specs/`**

Ensure the filenames remain unchanged.

- [ ] **Step 2: Move the existing plan files into `docs/ledger/plans/`**

Ensure the filenames remain unchanged.

- [ ] **Step 3: Leave no duplicate source of truth**

After migration, the ledger should be the authoritative location for these records.

### Task 3: Update repository documentation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Update the project layout section**

Replace the `docs/superpowers/` reference with `docs/ledger/`.

- [ ] **Step 2: Ensure the README language matches the ledger model**

Refer to the ledger as the home for planning and execution records.

### Task 4: Record the migration work

**Files:**
- Create: `docs/ledger/work-log/2026-06-24-ledger-migration.md`

- [ ] **Step 1: Write the work log entry**

Include:

- what was created
- what was migrated
- what documentation was updated

### Task 5: Verify the resulting structure

**Files:**
- Test: `docs/ledger/README.md`
- Test: `docs/ledger/specs/2026-06-24-documentation-design.md`
- Test: `docs/ledger/specs/2026-06-24-ledger-design.md`
- Test: `docs/ledger/plans/2026-06-24-documentation-set.md`
- Test: `docs/ledger/plans/2026-06-24-ledger-migration.md`
- Test: `docs/ledger/work-log/2026-06-24-ledger-migration.md`
- Test: `README.md`

- [ ] **Step 1: Review the migrated files and links**

Check that the files exist in the ledger and that the README points to the correct location.

- [ ] **Step 2: Check for leftover conflicting references**

Search for `docs/superpowers` references and remove or update any that should now point to the ledger.
