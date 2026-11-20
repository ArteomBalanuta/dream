# Work log: the cursor redesign

- Purpose: a richer gothic cursor, the same cursor in every menu and in play, and a small sword over an enemy.
- Status: on `integrate/cursor`, not merged.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery`: contracts frozen first, three
  implementers on disjoint files (art, enemy hover, drawing everywhere), integration, one review fix loop.

## What landed

- **Art** (`tools/ui-assets/gen_hud.py`). The gauntlet is now blued steel with fluted lames, rivets, an engraved
  gold cuff band and worn bright edges. `cursor.attack` is a standalone small sword: fuller, edge glint, gold
  crossguard, leather grip, pommel. Pivots are derived from the art (`tip_pivot`): the furthest solid pixel
  along the pointing axis. The old pointer pivot was 8 art px behind the drawn fingertip, and the sword's was 3.6.
  `check_hud.py` gates the sword's area, its silhouette against the pointer and the tip distance, each with a
  synthetic negative.
- **Enemy hover** (`EnemyHoverProbe`, `WorldCursorPicker.PickHoveredEnemy`). This is the same ray, wall rule and
  live check a right click uses. A test proves they agree in 8 cases. It runs every update without allocating
  (`AllocationAssert.AtMost(0)`). The flag travels `Render.PointerOverEnemy` -> `ClassicHudFrame` -> `HudView`.
- **One cursor everywhere** (`UiCursor`). The HUD, `MenuUiRenderer` (title, options, multiplayer, pause, loading)
  and the ImGui startup screen all draw through it at the `UiScale.Resolve` layout scale. The OS cursor is hidden
  only when a sprite cursor will draw this frame and the window is focused (`ClassicCursorPolicy`).
  `ImGuiDrawsCursor` keeps the ImGui arrow off under the pause menu.

## Traps found in review

- Hiding the OS cursor under the pause menu exposed ImGui's own software arrow, which had always been there,
  covered by the OS arrow.
- Esc during loading draws the loading overlay and the pause menu in one frame. Both drew a cursor.
- A pivot gate that accepts "any opaque pixel within 2 px" passes on the halo. Gate against the solid tip.

## Verification

- Full suite on `integrate/cursor`: 10569 total, 0 failed.
- Captures: the sword's point sits on the wolf, the gauntlet shows on the menu button hover, and the golden scenes
  changed only under the cursor (baselines re-recorded).
- Not exercised by a capture: the pause menu, multiplayer and startup screens with a live focused mouse (the
  harness starts unfocused); these are covered by `CursorOwnershipTests` and code review.
