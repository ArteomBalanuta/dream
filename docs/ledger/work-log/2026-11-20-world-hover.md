# Work log: unified world hover and the hover info panel

- Purpose: one answer to "what world thing is under the cursor" (enemy, NPC, ground item, remote player), shared by
  the cursor, the cursor aim, the right click and the zoom anchor, plus a kind-agnostic info panel for it.
- Status: on `integrate/world-hover`, not merged.
- Method: the `CLAUDE.md` team workflow with `parallel-agent-delivery`. The Lead froze the contracts
  (`996b0074`) and three implementers built in parallel on disjoint files: hover core, info descriptors, HUD
  presentation. The Lead wired the hot paths, two reviewers split by domain, then two fix loops, the second a
  re-review of the combined fix diff.
- Design: `docs/ledger/plans/2026-11-20-world-hover-and-info-panel.md`; audit:
  `docs/ledger/analysis/2026-11-20-world-hover-audit.md`.

## What landed

- **Hover core** (`dream/Client/Hover/`). `WorldHover` is the one facade the scene calls each update.
  - Precedence: gates first, then a hovered clickable label beats the ray, then UI, then the ray.
  - The pick is throttled. It re-picks on a pointer move over 2 px, a camera change, a button press, or every
    0.10 s. Between picks it only re-validates the target by id.
  - Hit volumes: enemies and players are capsules (enemies at the render pose), ground items are spheres. Nothing
    behind the first wall counts.
  - Resolution: nearest depth, then within 0.35 m the kind priority, then the lower id. This is order independent.
  - A sticky target keeps a 1.25x volume.
  - `HoverTracker` gives enter/stay/exit plus the panel's dwell (0.10 s), warm swap, linger (0.15 s + 0.10 s fade)
    and defeated hold (0.25 s).
- **Descriptors** (`HoverInfoComposer`, `Providers/*`) fill one fixed-capacity `HoverInfo` from live state each
  frame, with value-keyed string caches (0 bytes per frame unchanged).
  - Enemy: name, level, health, state, `[LMB] Attack`.
  - NPC: name and talk hint. Ground item: tier-coloured name, rarity and the pick-up or claimed hint.
  - Remote player: name, level, health.
- **HUD** (`HoverInfoPanel`, `HoverPanelPlacement`). The panel is non-interactive and draws only from the view
  model.
  - Placement: below-right of the cursor, flipping and clamping, clear of the bar, the windows and the cursor
    sprite.
  - The hovered actor's floating label hides while the panel draws.
  - New cursors: `cursor.talk`, `cursor.take`, `cursor.use`, with gates and synthetic negatives in `check_hud.py`.
- **Wiring**: right click and the aim read the hover. `ClickMoveHost.TryPick` returns the hovered enemy or a
  ground pick with no enemy resolution of its own, so a click hits what the cursor shows. `EnemyHoverProbe` is
  retired.

## Found on the way

- `EnemyKind`, `AttackDamage` and `AttackIntervalSeconds` are not replicated: the client holds defaults. The enemy
  panel therefore shows no kind, damage or speed. Neither enemies nor remote players replicate mana, so no mana bar
  appears until a protocol change (current 22).
- A label hover read from the HUD survived frames the classic HUD did not build (a fault, a skipped frame, the
  ImGui surface) and pinned the world hover to that NPC. It is now reset and gated on a built frame, with tests.
- The pairwise "is this hit better" fold made the 0.35 m tie window chain, so the answer depended on source order.
  It is now a window from the nearest hit, tested in all six orders.
- The panel's quad reserve was a restated number, and a stat line counted as one string instead of two. Every
  string is now capped by construction. A real-batch test at every UI scale measures the bound; without the cap it
  emits 2352 quads against 849.
- `check_hud.py`'s solid-pixel negative matched the tip-distance message too, so it could not fail. It now matches
  exactly.

## Verification

- Full suite: 11002 passed, 0 failed, 47 skipped, twice (`f865346b` and `3288eda6`). The test dll was confirmed
  rebuilt and the new tests present. Fix loop 2 (`c7e67285`) was verified with 151 targeted tests and two proven
  negatives, not a third full run.
- Captures (`artifacts/captures/hover-wolf-noon`, `-night`): the sword sits on the wolf, the Wolf panel is
  below-right, the wolf's label is hidden, and it is legible at night. `hover-enemy-noon` shows the linger and exit
  after the wolf walks off the latched pointer.
- Golden: the world snaps pass in all three scenes, including `wolf-staged`. `01-play` fails in all three, and
  fails the same way on untouched master `4d404c81` (spawn-noon: pixel 0.00992 vs 0.00981 here, the same block
  @32,304 with 106 over). The baseline predates master's Combat-mode default, so it is stale on master and was not
  re-recorded here.
- Not exercised in a live capture: NPC, ground item and remote player hover. These are covered by unit tests and
  headless `HudPreviewTests` PNGs; no capture step points the cursor at an NPC.

## Follow-ups

- `WorldCursorPicker.PickHoveredEnemy` is now unused by the game (kept, tested).
- Replicate enemy kind, damage, speed and mana (a protocol bump), then the panel shows them with no HUD change.
- Highlight tint on the hovered model (wave 2 of the plan), and an `IHoverSource` for interactables.
