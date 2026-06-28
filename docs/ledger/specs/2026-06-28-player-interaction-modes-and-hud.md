Purpose: Define a clean gameplay interaction-mode split so primary use no longer triggers both building and combat behavior, and specify the HUD updates that expose player health and active mode.
Status: Approved in chat, pending implementation planning.
Scope: Gameplay input routing, top-left HUD status content, and targeted verification for the mode split.

# Summary

The current gameplay input path routes a single primary-use trigger into two unrelated actions in the same frame: cube placement and combat feedback. This spec introduces an explicit gameplay interaction mode so primary use performs exactly one action at a time. It also upgrades the existing top-left HUD panel to show useful player state instead of placeholder text.

# Goals

- prevent left click from spawning a cube and applying combat feedback in the same gameplay frame
- add an explicit gameplay interaction mode with at least `Building` and `Combat`
- expose player HP and active mode in the existing top-left HUD
- keep gameplay state ownership outside rendering code
- preserve current settings-menu input suppression behavior

# Non-Goals

- redesign the settings window
- add new combat mechanics beyond the current demo combat action
- introduce automatic context-sensitive interaction inference
- move gameplay HUD content into a separate overlay panel

# Design

## State Model

The project keeps `RuntimeMode` for high-level application state such as gameplay versus settings menu. A separate gameplay-level state is added for interaction intent, for example `PlayerInteractionMode.Building` and `PlayerInteractionMode.Combat`.

This interaction mode is independent from menu state:

- `RuntimeMode` answers whether gameplay input is allowed at all
- `PlayerInteractionMode` answers what the primary-use action should do when gameplay input is allowed

`PlayScene` owns the active interaction mode because it already coordinates frame-level gameplay behavior and HUD request application.

## Input Behavior

Direct selection keys are used instead of a toggle:

- `1` selects `Building`
- `2` selects `Combat`

When gameplay input is enabled:

- left click in `Building` performs build placement only
- left click in `Combat` performs combat primary use only

When the settings menu is open, gameplay movement, look, and primary use remain suppressed exactly as they are today.

## Responsibilities

### `PlayScene`

`PlayScene` becomes the single place that:

- updates the active interaction mode from hotkeys
- reads the primary-use trigger
- routes that trigger into exactly one gameplay action based on the active mode

This avoids keeping mode-switch logic in rendering code or duplicating action routing across player subsystems.

### Player and Interaction Classes

`PlayerController` captures the mode hotkeys alongside the existing movement and interaction inputs.

`PlayerInteraction` remains focused on low-level build interaction execution rather than owning gameplay mode policy. Combat triggering remains coordinated by `PlayScene`, which is also where the current combat feedback event is published.

### HUD and Rendering

The HUD continues to use the existing always-visible top-left panel. No second status overlay is introduced.

That panel should show:

- player HP in `current/max` form
- active interaction mode
- the existing `Esc` settings hint

Low-value runtime/debug lines such as resolution may remain for now, but player-facing status should appear first in the panel. Rendering code reads display-ready values and does not decide gameplay behavior.

# Data Flow

1. `PlayScene` reads runtime input state each frame.
2. If gameplay input is allowed, `PlayScene` updates the active interaction mode from direct-selection hotkeys.
3. `PlayScene` reads the primary-use trigger.
4. `PlayScene` dispatches that trigger to exactly one path:
   - `Building` -> build placement
   - `Combat` -> combat primary-use feedback path
5. HUD rendering reads current player health and active interaction mode and displays them in the top-left panel.

# Error Handling And Edge Cases

- If the mode hotkey is pressed while the settings menu is open, it should not change gameplay behavior until gameplay input is allowed again.
- If the active mode is unavailable in the future because of feature gating, the routing layer should fail closed rather than falling back to multiple actions.
- HUD rendering should tolerate missing or unchanged gameplay values and continue drawing the panel without throwing.

# Testing

Implementation should add or update focused tests for:

- direct mode selection (`1` -> `Building`, `2` -> `Combat`)
- primary use triggering only the selected mode action
- settings menu continuing to suppress gameplay interaction
- HUD data reflecting player HP and active interaction mode

# Implementation Notes

- Keep the change narrow and avoid coupling gameplay policy into `Render` or `HudRenderer`.
- Reuse the existing top-left HUD block instead of creating another gameplay panel.
- Prefer naming that leaves room for future modes such as interaction, harvesting, or tool use.
