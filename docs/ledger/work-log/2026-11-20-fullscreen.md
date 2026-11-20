# 2026-11-20: fullscreen / window mode

Branch `integrate/fullscreen`, on top of `integrate/tactical-camera` (neither merged).

## What was built

- `window_mode` config key (`windowed` | `borderless` | `fullscreen`, default windowed; unknown -> windowed) and a
  "Display Mode" row on the main Options page under UI Scale. The row writes the config while dragged and applies the
  mode once, on release, only if it changed.
- `WindowModeController` / `WindowModeState` (`dream/Application/WindowModeController.cs`): placement per mode on the
  monitor the window is on (borderless = undecorated window at the monitor's position and resolution; fullscreen =
  exclusive at the current video mode), toggle rule, and the remembered windowed placement so leaving fullscreen
  restores the exact size and position.
- `Game`: applies the saved mode at startup; F11 and Alt+Enter (either Alt, keypad Enter too, no repeats) toggle
  between windowed and the last fullscreen mode in every scene and save the config. Exclusive fullscreen is detected
  from GLFW's window monitor, not OpenTK's `WindowState`, which can read Minimized/Normal after alt-tab. `OnResize`
  ignores 0x0 (exclusive fullscreen iconifies on alt-tab). The dev resolution presets do nothing outside windowed.
  The camera's fullscreen-only cursor clip follows the mode.
- Alt+Enter never acts as Enter: the menu (handled before the menu mapping), chat (`ChatInput.IsChatEnter`) and the
  ImGui connection screen (a latch, so releasing Alt first still does not press Enter).
- Capture runs (`DREAM_HUD_CAPTURE`, `DREAM_MENU_CAPTURE`) never change the window or save `window_mode`.

## Verification

- Full suite on `50ae0bd8`: 10765 passed, 0 failed. Review fixes after it: filtered 620/0.
- Golden: every scene frame passes (0.000000, 0.000000, 0.000003); `01-play` still fails on the stale baselines, the
  same as master (see `2026-11-20-tactical-camera.md`).
- Menu capture: the Options panel fits with Display Mode under UI Scale.

## Needs a hand test

Switching modes from the row, F11 and Alt+Enter on the title menu, connection screen and in play; exact windowed
restore; alt-tab out of exclusive fullscreen and back, then F11; startup with a saved fullscreen/borderless mode; a
second monitor; the cursor clip in borderless. The capture harness cannot exercise a real window mode change.
