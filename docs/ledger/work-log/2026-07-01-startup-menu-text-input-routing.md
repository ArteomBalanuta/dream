Purpose: record the startup connection menu text-input fix
Status: implemented
Scope: OpenTK text input, startup ImGui input fields, keyboard editing keys, focused regression coverage

## Root Cause

The startup connection screen used `ImGui.InputText(...)` for host, port, and player-name fields, but the desktop shell did not forward OpenTK text-input events to the startup ImGui context. `StartupMenuRenderer` also only applied mouse state each frame, so text fields could be focused but typed characters and editing keys had no reliable input route.

## Changes

- Added `ImGuiTextInputQueue` as a small boundary object for queued Unicode text input.
- `Game.OnTextInput(...)` now forwards startup text characters into the active startup scene.
- `StartupConnectionScene` forwards queued text input to `StartupMenuRenderer`.
- `StartupMenuRenderer.BeginFrame(...)` now sets its ImGui context, applies keyboard modifier/editing-key state, and drains queued text characters through `io.AddInputCharacter(...)` before rendering `InputText` fields.
- Startup rendering now receives `KeyboardState` in addition to `MouseState`.

## Verification

Focused coverage was added for the text-input queue and run together with startup connection option/coordinator tests.

