# First-Person Viewmodels And Skins Design

**Purpose:** Define a clean foundation for first-person hands and held-item cosmetics in `dream` without growing `Render.cs` into a larger godclass.

**Audience:** Developers extending the renderer/gameplay architecture and future contributors adding cosmetic assets, hands, or held items.

**Scope:** Renderer boundaries, runtime viewmodel state, asset/catalog design, animation triggering, and first-release behavior for skinnable hands plus a separately swappable held-item slot.

## Problem Statement

The project can already load and render animated glTF models through `AnimatedModel`, but it does not yet support first-person player presentation as a real subsystem.

Current gaps:

- no dedicated first-person render pass
- no concept of a `Hands` slot or `HeldItem` slot
- no runtime controller that translates player input into presentation animation
- no data-driven skin catalog for viewmodel assets
- no real named animation clip selection in `AnimatedModel`

At the same time, `dream/Render.cs` is already dense. It owns shared OpenGL state, shader loading, world draw helpers, HUD crosshair resources, ImGui setup, projection updates, and parts of collision debug drawing. Adding hands and skins directly into that file would solve the immediate feature while making the rendering architecture harder to evolve.

## Design Goals

- Add first-person hands and held-item presentation as a real subsystem rather than a one-off feature.
- Support interchangeable cosmetics from day one through data-driven definitions.
- Keep first-person presentation separate from world geometry, physics, and HUD logic.
- Prevent further godclass growth in `Render.cs`.
- Reuse the existing glTF runtime model support where practical.
- Keep the first release intentionally narrow and stable.

## Non-Goals

- full inventory gameplay
- world-model and first-person-model pairing rules
- procedural IK or advanced hand placement systems
- multiplayer cosmetic replication
- a large animation state machine with blending layers
- a broad renderer rewrite beyond the seams needed for this feature

## Recommended Approach

Introduce a dedicated first-person viewmodel subsystem built around a separate render pass and a small runtime controller.

This subsystem should render after the world and debug passes, but before HUD/ImGui. It should use the gameplay camera orientation while owning its own projection matrix and first-person tuning values. That separation allows hands and held items to look good independently of the world camera FOV and prevents the world render path from becoming responsible for first-person presentation rules.

The implementation should not add feature logic directly into `Render.cs` other than minimal shared low-level support. New first-person behavior should live in focused collaborators.

## Architecture

### Rendering order

The target frame order is:

1. World
2. Collision/debug overlays
3. First-person viewmodels
4. HUD / ImGui

This keeps first-person presentation visually above the world while preserving the existing HUD layering model.

### New viewmodel units

The first-person feature should be split into the following units:

- `PlayerViewModelController`
  - owns runtime presentation state
  - translates input/gameplay events into animation requests
  - manages active `Hands` and `HeldItem` slot selections
- `ViewModelRenderer`
  - owns the first-person render pass
  - renders slot content in a dedicated order
  - applies viewmodel-specific projection/FOV and transform offsets
- `ViewModelCatalog`
  - resolves cosmetic ids into runtime definitions
  - centralizes asset declarations rather than scattering paths across scene code
- `ViewModelDefinition`
  - describes a single viewmodel asset and its behavior metadata
- `ViewModelLoadout`
  - holds the currently selected hands skin id and held-item id

### Render boundary rule

`Render.cs` may continue to own shared low-level OpenGL resources and generic model draw support, but feature-specific first-person rules must be extracted instead of appended inline.

The explicit rule for this feature is:

- do not add hands/skins business logic directly into `Render.cs`
- do allow small shared support methods if they are renderer-generic and useful outside one feature
- move new first-person behavior into `ViewModelRenderer` and related collaborators

This keeps the feature aligned with the broader renderer-layering direction already documented in the repo ledger.

## Runtime Data Model

### ViewModelDefinition

Each first-person cosmetic entry should describe:

- stable id
- slot type: `Hands` or `HeldItem`
- model asset path
- optional idle animation clip name
- optional use/click animation clip name
- local transform offsets
- local rotation offsets
- local scale

This definition should be data-oriented enough that adding a new skin or held item is primarily a catalog change rather than a renderer code change.

### ViewModelLoadout

The active loadout should contain:

- current hands skin id
- current held-item id

This is intentionally small for the first iteration while still leaving room for later persistence or settings integration.

### Catalog responsibilities

The catalog should:

- register built-in hands and held-item definitions
- validate lookups by id
- expose the default loadout for startup

It should not own runtime animation state or rendering code.

## Input And Animation Flow

The clean data flow is:

`PlayerController` / interaction input -> `PlayerViewModelController` -> `ViewModelState` -> `ViewModelRenderer`

Behavior rules:

- idle presentation should play when no action animation is active
- left mouse click should trigger a use/click animation on the hands slot
- the held-item slot may optionally play a matching use animation if defined
- equip changes should swap runtime definitions and reset relevant presentation state

`PlayerViewModelController` should own these transitions. Rendering code should consume already-decided presentation state rather than deciding gameplay behavior itself.

## AnimatedModel Requirements

The current `AnimatedModel` implementation is a workable base, but it needs one important improvement before it can support interchangeable viewmodels cleanly.

Required change:

- real named clip selection instead of `StartAnimation(string name = null)` only enabling a generic animation flag

The viewmodel system needs to be able to say:

- play idle clip for hands
- play use clip for hands
- play use clip for held item if present

That implies explicit ownership of the currently active clip name or resolved animation handle inside the runtime model path.

This feature does not require a full animation state machine. A small deterministic model is enough:

- one active clip per model
- optional looping for idle
- one-shot playback for use/click
- return to idle when the one-shot finishes

## First Release Scope

The first release should support:

- one visible first-person hands skin
- one visible held-item skin
- separate asset definitions for both slots
- idle animation
- click/use animation
- per-slot transform offsets
- runtime-ready loadout state

The first release does not need:

- a polished skin-selection menu
- multiple gameplay items with unique mechanics
- world pickup/equip systems
- advanced blending or procedural animation

## Settings And Future Extensibility

The system should be built so that future runtime settings can naturally add:

- viewmodel FOV
- per-slot offset tuning
- active skin selection
- debugging toggles for first-person presentation

Those controls do not all need to ship in the first release, but the architecture should not block them.

## Testing Strategy

Testing should focus on the new boundaries rather than trying to visually assert all rendering behavior.

Priority coverage:

- catalog lookup tests for valid and invalid cosmetic ids
- controller tests for click/use transition behavior
- controller tests for idle fallback after one-shot completion
- loadout/default selection tests
- any pure transform/layout helpers introduced for the viewmodel pass

Manual verification should confirm:

- hands are visible in first person
- held item renders independently of hands
- click animation triggers reliably
- world rendering and HUD behavior still work
- nearby world geometry does not make the hands feel incorrectly clipped

## Risks And Mitigations

### Risk: `Render.cs` keeps growing anyway

Mitigation:

- make renderer extraction an explicit phase before adding visible hands
- reject designs that require adding substantial feature logic inline to `Render.cs`

### Risk: animation ownership stays ambiguous

Mitigation:

- define active clip selection and one-shot versus looping semantics before wiring visible assets

### Risk: viewmodel data becomes another hardcoded asset tangle

Mitigation:

- centralize definitions in a catalog with stable ids and runtime-resolved asset paths

## Implementation Sequence

The intended order for implementation is:

1. Extract the first-person renderer seam so `Render.cs` does not absorb new feature logic.
2. Introduce the runtime viewmodel controller and loadout/catalog model.
3. Improve `AnimatedModel` clip selection and simple playback control.
4. Add the first hands skin and held-item skin plus click/idle behavior.
5. Add runtime-facing hooks for future swapping and tuning.

## Success Criteria

This design is successful when:

- first-person hands and a held item can both render in a dedicated pass
- the active hands and held-item assets are selected by id through catalog data
- left click visibly triggers use animation
- idle presentation resumes when the action animation completes
- adding another skin or held item requires data registration more than renderer rewrites
- `Render.cs` remains a shared low-level renderer host rather than becoming the home of first-person feature logic
