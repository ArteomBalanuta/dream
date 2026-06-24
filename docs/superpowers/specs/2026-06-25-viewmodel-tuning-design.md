# Viewmodel Tuning And Body-Anchor Illusion Design

Date: 2026-06-25
Status: Drafted and reviewed
Owner: Codex

## Summary

Add a runtime-tunable first-person viewmodel rig that:

- exposes a shared `viewmodel_fov` and shared rig offsets/rotations
- supports per-slot tuning for `hands` and `held item`
- makes the arms feel attached to the player body through a lightweight body-anchor illusion
- persists through the existing settings flow
- keeps pose math out of `Render.cs`

The first implementation should prioritize fast in-game iteration and clean ownership boundaries over a full first-person body system.

## Goals

- Make the current hands feel lower, wider, and less disconnected from the player.
- Support Source-style runtime tweaking with menu sliders.
- Keep a clear architectural split between settings UI, pose resolution, and rendering.
- Preserve live runtime adjustment and persistence.
- Ship better defaults so the current experience improves even before manual tuning.

## Non-Goals

- Adding a true local first-person body mesh.
- Building a preset library or skin authoring workflow in this pass.
- Replacing the current animation system.
- Moving viewmodel logic into world-space character simulation.

## User Decisions Captured

- Tuning model: `B` shared base plus separate `hands` and `held item` overrides.
- Body connection strategy: visual illusion, not a true player body representation.

## Design Overview

The viewmodel pose should be resolved from layered data instead of hardcoded per-asset transforms alone.

Resolution order:

1. Asset defaults
2. Shared runtime rig tuning
3. Body-anchor illusion offsets
4. Per-slot runtime tuning
5. Animation offsets

This preserves a stable global first-person framing pass while still allowing `hands` and `held item` to be corrected independently.

## Architecture

### Settings Data

Persist viewmodel tuning in `GameSettings` using dedicated structured types rather than loose float fields.

Suggested types:

- `ViewModelRigSettings`
- `ViewModelSlotTuning`
- `ViewModelBodyAnchorSettings`

`ViewModelRigSettings` should contain:

- `FieldOfViewDegrees`
- shared `PositionOffset`
- shared `RotationOffsetDegrees`
- `Hands`
- `HeldItem`
- `BodyAnchor`

`ViewModelSlotTuning` should contain:

- local `PositionOffset`
- local `RotationOffsetDegrees`

`ViewModelBodyAnchorSettings` should contain:

- `HorizontalSpread`
- `VerticalDrop`
- `ForwardPull`

The body-anchor settings are intended to influence arm origin and framing, not replace normal slot tuning.

### Pose Resolution

Add a focused helper near the existing viewmodel pipeline, for example `ViewModelPoseResolver`.

Responsibilities:

- compose the final shared and slot-local transforms
- apply body-anchor illusion offsets before slot-local tuning
- prepare the final per-slot pose data consumed by rendering
- keep transform composition deterministic and inspectable

This helper should be the single place where the final pose stack is assembled.

### Rendering Boundary

`ViewModelRenderer` should remain a renderer:

- consume the resolved pose
- use the viewmodel-specific FOV
- draw the slots in the dedicated viewmodel pass

It should not become the place where UI-specific tuning decisions or special-case hand math accumulates.

### UI Boundary

`Render.cs` should only:

- display the runtime controls
- forward edited values into settings
- expose a reset action

It should not own the transform composition logic.

## Runtime Settings UX

Expose a `Viewmodel` section in the runtime menu.

### Shared Rig Controls

Primary controls:

- `Viewmodel FOV`
- `Offset X`
- `Offset Y`
- `Offset Z`
- `Pitch`
- `Yaw`
- `Roll`

These define the broad framing and should be tuned first.

### Body Anchor Controls

Dedicated controls:

- `Body Anchor Spread`
- `Body Anchor Drop`
- `Body Anchor Forward Pull`

These settings create the torso-origin illusion by biasing the arms downward and outward before slot corrections are applied.

### Slot Groups

Two collapsible groups:

- `Hands`
- `Held Item`

Each group exposes:

- `Offset X`
- `Offset Y`
- `Offset Z`
- `Pitch`
- `Yaw`
- `Roll`

This keeps the workflow understandable:

1. tune shared framing
2. tune body-origin feel
3. correct `hands`
4. correct `held item`

### Reset

Add `Reset Viewmodel Tuning` to restore known-good defaults for the entire viewmodel rig.

## Defaults

The first shipped defaults should visibly improve the current experience without manual tuning.

Default intent:

- hands start lower on the screen
- hands start wider from the centerline
- hands angle inward slightly
- held item sits within the same broader framing instead of floating independently

Exact numbers can be finalized during implementation, but they should be expressed through the new layered settings model rather than hidden in ad hoc renderer constants.

## Risks And Mitigations

### Slider overload

Risk:
Too many sliders can make the menu hard to understand.

Mitigation:
Group controls into `Shared Rig`, `Body Anchor`, `Hands`, and `Held Item`, with sensible defaults and collapsible sections.

### Hands and held item drifting apart

Risk:
Separate tuning can cause the held item to stop reading as attached to the hands.

Mitigation:
Keep the shared rig as the dominant framing pass and reserve slot tuning for correction, not full replacement of the pose.

### Render.cs density

Risk:
The settings feature could push more behavior into an already important file.

Mitigation:
Confine `Render.cs` to menu presentation and keep composition logic in a dedicated pose resolver.

## Verification

Code verification:

- `dotnet build dream\\dream.csproj --nologo`
- `dotnet test dream.Tests\\dream.Tests.csproj --nologo`

In-game verification:

- default hands appear lower and wider than the current version
- `Viewmodel FOV` affects only the viewmodel pass
- shared rig sliders update live
- `hands` and `held item` tuning update live and persist
- body-anchor sliders make the arms feel more torso-connected
- reset returns the rig to known-good defaults
- no new tuning math is embedded directly into `Render.cs`

## Implementation Notes

- Favor reusing existing viewmodel data structures where possible, but do not overload them with mixed responsibilities.
- Keep tuning settings serializable through the current settings persistence flow.
- Use the new rig model as the future base for later polish such as click sway, skin-specific defaults, or per-item presets.

## Out Of Scope Follow-Ups

- true first-person body rendering
- preset save/load management
- weapon-specific authored animation sets
- skin-specific rig profiles
