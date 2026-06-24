# Dream Documentation Design

**Date:** 2026-06-24

**Goal:** Create a quickstart-first documentation set for the current `dream` project that serves both new users and developers while describing the codebase as it exists today.

## Audience

- New users who need to build, run, and understand the basic controls quickly.
- Developers who need a map of the codebase, architecture, dependencies, and runtime assumptions.

## Documentation Principles

- Lead with practical onboarding before internal architecture.
- Document the current implementation faithfully rather than proposing improvements.
- Keep the top-level entry short and task-oriented.
- Move deeper technical details into focused reference documents under `docs/`.
- Avoid speculative or cleanup-oriented sections.

## Proposed Documentation Set

### `README.md`

Purpose:
- Introduce the project at a high level.
- Provide prerequisites and build/run steps.
- Document current controls and expected runtime behavior.
- Link readers to deeper reference documents.

Planned sections:
- Project summary
- Tech snapshot
- Prerequisites
- Build and run
- Controls
- Project layout
- Further reading

### `docs/overview.md`

Purpose:
- Explain the project in plain language for readers who want more context after the README.
- Summarize how the application behaves at runtime.

Planned sections:
- What the project is
- Core gameplay/rendering loop
- Main systems
- Current assets and visual elements

### `docs/architecture.md`

Purpose:
- Describe subsystem responsibilities and interactions for developers.

Planned sections:
- Startup flow from `Program` to `Game`
- Update loop responsibilities
- Render loop responsibilities
- Physics integration model
- Relationships between `Game`, `Player`, `Map`, `PhysicsIntegrator`, `Render`, `Shader`, and `AnimatedModel`

### `docs/project-structure.md`

Purpose:
- Provide a practical file and folder guide.

Planned sections:
- Solution and project files
- Core gameplay/source files
- Rendering and shader files
- Assets and content folders

### `docs/configuration.md`

Purpose:
- Capture project setup and runtime assumptions that affect local execution.

Planned sections:
- Target framework
- NuGet dependencies
- Asset loading expectations
- Shader/model/font file locations
- Window/runtime assumptions
- Build verification notes

### `docs/rendering-and-assets.md`

Purpose:
- Document how rendering resources and content are used today.

Planned sections:
- Shader usage
- Cube rendering path
- HUD and ImGui usage
- glTF model loading
- Texture handling
- Asset directory summary

## Source Material To Reflect

The documentation should reflect the current codebase, including:

- `.NET 8` project configuration from `dream/dream.csproj`
- Entry point in `dream/Program.cs`
- Main window and orchestration in `dream/Game.cs`
- Rendering and HUD behavior in `dream/Render.cs`
- Physics setup in `dream/PhysicsIntegrator.cs`
- Input and player movement in `dream/Player.cs`
- Map/grid initialization in `dream/Map.cs`
- Camera behavior in `dream/Camera.cs`
- Shader loading in `dream/Shader.cs`
- Asset/model loading in `dream/AnimatedModel.cs`
- Available shaders, fonts, textures, and model files in the repository

## Style Decisions

- `README.md` should be concise and newcomer-friendly.
- Reference docs should prefer short sections and direct statements over long prose.
- Terminology should match the code so developers can move from docs to source files easily.
- Any important current limitations that are necessary to understand setup should be stated factually, without turning the docs into a remediation plan.

## Accepted Design Decisions

- Primary audience: developers and new users
- Documentation style: quickstart-first
- Documentation scope: as-is only, without improvement-oriented sections
- Documentation structure: layered docs with a top-level `README.md` and focused files under `docs/`
