# Dream Documentation Set Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create a quickstart-first documentation set for the current `dream` project that helps new users run it and helps developers understand its structure and runtime assumptions.

**Architecture:** The documentation will use a layered structure. `README.md` will act as the onboarding entry point, while focused Markdown files under `docs/` will cover overview, architecture, structure, configuration, and rendering/assets in more detail.

**Tech Stack:** Markdown documentation, .NET 8 project metadata, C# source analysis, existing repository assets and shaders

---

### Task 1: Gather verified project facts for the docs

**Files:**
- Create: `docs/superpowers/plans/2026-06-24-documentation-set.md`
- Modify: `README.md`
- Modify: `docs/overview.md`
- Modify: `docs/architecture.md`
- Modify: `docs/project-structure.md`
- Modify: `docs/configuration.md`
- Modify: `docs/rendering-and-assets.md`

- [ ] **Step 1: Verify the source files and build status**

Run: `dotnet build dream\dream.csproj`
Expected: `Build succeeded.` with warnings only

- [ ] **Step 2: Record the repo facts to reflect in the docs**

Capture these facts from the codebase:

- Project target: `.NET 8`
- Entry point: `Program.Main()` creates and runs `Game`
- Main systems: `Game`, `Player`, `Map`, `PhysicsIntegrator`, `Render`, `Shader`, `AnimatedModel`
- Controls: `W`, `A`, `S`, `D`, mouse look, `Space`, left mouse button, `F`
- Assets: shaders in `dream/shaders`, models/textures in `dream/animations`, font in `dream/fonts`
- Runtime assumption: several shader/model paths are hardcoded to absolute Windows paths in source

- [ ] **Step 3: Use the verified facts as the only basis for the documentation**

Write the docs to describe the project exactly as implemented, avoiding speculative improvements or cleanup recommendations.

### Task 2: Write the onboarding entry point

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write the `README.md` content**

Include:

- Project summary
- Tech snapshot
- Prerequisites
- Build and run commands
- Current controls
- Short project layout
- Links to the deeper docs

- [ ] **Step 2: Keep the README optimized for first-time readers**

Use short sections and command-oriented language so a new user can find setup and controls quickly.

### Task 3: Write the reference documentation set

**Files:**
- Create: `docs/overview.md`
- Create: `docs/architecture.md`
- Create: `docs/project-structure.md`
- Create: `docs/configuration.md`
- Create: `docs/rendering-and-assets.md`

- [ ] **Step 1: Write `docs/overview.md`**

Summarize the project purpose, runtime loop, main systems, and current visuals/assets.

- [ ] **Step 2: Write `docs/architecture.md`**

Describe startup, update flow, render flow, and subsystem interactions.

- [ ] **Step 3: Write `docs/project-structure.md`**

Document the solution/project files, source files, shaders, models, textures, and fonts.

- [ ] **Step 4: Write `docs/configuration.md`**

Document the target framework, NuGet packages, build verification result, and current runtime path assumptions.

- [ ] **Step 5: Write `docs/rendering-and-assets.md`**

Document shader usage, cube rendering, HUD/ImGui behavior, model loading, texture handling, and asset locations.

### Task 4: Verify and polish the docs

**Files:**
- Test: `README.md`
- Test: `docs/overview.md`
- Test: `docs/architecture.md`
- Test: `docs/project-structure.md`
- Test: `docs/configuration.md`
- Test: `docs/rendering-and-assets.md`

- [ ] **Step 1: Review all docs for consistency with the code**

Check that file names, class names, controls, and paths match the repository exactly.

- [ ] **Step 2: Review the docs for readability**

Check that `README.md` stays short and that deeper docs avoid duplication where possible.

- [ ] **Step 3: Re-run build verification if needed**

Run: `dotnet build dream\dream.csproj`
Expected: `Build succeeded.` with warnings only
