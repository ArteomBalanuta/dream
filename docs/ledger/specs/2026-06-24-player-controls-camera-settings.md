# Player Controls, Camera, And Settings Design

**Purpose:** Define the intended target state for fixing player control responsiveness, camera framing, and runtime settings usability in `dream`.

**Status:** Drafted from current analysis

**Scope:** Player movement/jump behavior, projection/camera tuning, and an in-game settings menu

## Goal

Make the project feel controllable and configurable by restoring reliable movement, correcting camera framing, and adding an interactive settings UI for core runtime options.

## Design Principles

- Fix player usability issues before pursuing broader engine work.
- Keep the solution bounded and incremental.
- Separate movement, camera, and settings responsibilities clearly enough to tune them independently.
- Prefer explicit runtime state over scattered hardcoded values.

## Target State

After this work:

- `W`, `A`, `S`, `D` movement works reliably
- jump behavior is responsive and debuggable
- the camera no longer feels artificially zoomed or corner-offset
- projection updates correctly with window size
- the user can access a settings panel to adjust core options

## Movement Design

### Immediate goal

Stabilize the existing physics-driven player movement instead of replacing it outright on the first pass.

### Required behavior

- movement input should reliably wake or activate the player body
- jump input should be gated by a grounded-state or similarly reliable rule
- movement tuning should be explicit and easy to change

### Implementation direction

Likely additions:

- explicit body activation on movement/jump input
- movement/jump tuning fields grouped more clearly
- lightweight diagnostics during debugging, removable or gateable later

## Camera And Projection Design

### Projection

The projection should:

- use the actual window aspect ratio
- update when the window is resized
- support configurable FOV

### Camera position

The camera should use a clearer eye-height offset relative to the player body, rather than a corner offset.

### Mouse tuning

Mouse sensitivity should become configurable through runtime settings.

## Settings UI Design

### Minimum settings scope

The first usable settings panel should support:

- FOV
- mouse sensitivity
- current resolution display

Preferred if feasible in the same pass:

- window mode or fullscreen toggle

### Interaction model

The current HUD is non-interactive. The new settings UI should:

- allow input when opened
- stay out of the way during normal gameplay
- preserve mouse-look gameplay when closed

### Runtime state

Introduce a dedicated settings state object or equivalent so values are not scattered between `Game`, `Render`, and `Camera`.

## Suggested Structural Changes

### New shared settings state

Introduce something like:

- `GameSettings`
- `RuntimeSettings`

Possible fields:

- `FieldOfView`
- `MouseSensitivity`
- `WindowWidth`
- `WindowHeight`
- `IsFullscreen`

### Render responsibilities

`Render` should expose a way to update projection settings rather than freezing them in the constructor.

### Game responsibilities

`Game` should own runtime window state and coordinate settings/UI toggling.

### Player responsibilities

`Player` should own movement and jump tuning, but not projection or window concerns.

## Verification Requirements

The implementation should verify:

1. `dotnet build dream\dream.csproj` still succeeds
2. movement input now changes player motion reliably
3. jump works under defined conditions
4. aspect ratio and FOV are no longer hardcoded incorrectly
5. a settings UI can be opened and used

## Scope Boundary

Included:

- movement reliability improvements
- jump handling improvements
- camera framing fixes
- projection resize/FOV support
- interactive settings panel

Deferred:

- full replacement with a custom character controller unless needed
- major scene/UI system redesign
