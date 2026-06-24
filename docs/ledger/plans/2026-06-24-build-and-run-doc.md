# Build And Run Documentation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a dedicated build/run guide for the `dream` project and link it from the repository README.

**Architecture:** The work will add one focused operational document at `docs/build-and-run.md` and make a minimal navigation update in `README.md`. The new doc will centralize build, run, test, warning, and local-artifact guidance without duplicating broader architecture docs.

**Tech Stack:** Markdown documentation, existing repository docs, current verified `dotnet` commands

---

### Task 1: Write the operational guide

**Files:**
- Create: `docs/build-and-run.md`

- [ ] **Step 1: Add prerequisites and command context**

Document:

- `.NET 8 SDK`
- Windows desktop environment with OpenGL support
- commands should be run from the repository root

- [ ] **Step 2: Add build, run, and test commands**

Document:

```powershell
dotnet build dream\dream.csproj
dotnet run --project dream\dream.csproj
dotnet test dream.Tests\dream.Tests.csproj
```

- [ ] **Step 3: Add runtime asset and warning notes**

Explain:

- assets are copied to output and resolved via `AssetPaths`
- build currently succeeds with known `BulletSharp` and `ImageSharp` warnings

- [ ] **Step 4: Add local artifact and troubleshooting notes**

Cover:

- `imgui.ini`
- `dream-run*.log`
- missing SDK
- warnings vs errors
- missing runtime asset output

### Task 2: Update README navigation

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a link to the new guide**

Place a link to `docs/build-and-run.md` in the further-reading section.

### Task 3: Verify the documentation

**Files:**
- Test: `docs/build-and-run.md`
- Test: `README.md`

- [ ] **Step 1: Verify file existence and link targets**

Check that:

- `docs/build-and-run.md` exists
- `README.md` links to it correctly

- [ ] **Step 2: Verify command accuracy against the current repo state**

Confirm the doc matches the already verified commands:

```powershell
dotnet build dream\dream.csproj
dotnet test dream.Tests\dream.Tests.csproj
```
