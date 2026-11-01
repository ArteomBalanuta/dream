# Classic HUD integration, 2026-11-01

Plan: `docs/ledger/plans/2026-11-01-classic-hud-plan.md`. Branch: `integrate/classic-hud`, from 1d4ede8.

## Merges

| Workstream | Branch tip | Merge | Conflicts |
|---|---|---|---|
| WS1 GL foundation | 6cc1858 | dfc06bb | none |
| WS2 surface, F3, router, view model | cfb551a | d4f1de6 | no text conflict; duplicate `DisposeClassicHud` resolved in the merge |
| WS3 status bar, notices | 4af0b05 | 37cf51f | none |
| WS4 windows | 2f90356 | b6e7fb5 | `IUiInput.cs`: both pointer members kept, unified in the glue commit |
| WS5 combat text, protocol v12 | a1ccffb | 0739337 | none |

Glue: 8440931. First GL run fixes and docs: a448687. Allocation tests: 1a074e1.

## Deviations from the plan and the brief

- **`UiBatch.UnitScale` is `UiLayout.Scale / 4`, not `Scale`.** Sprites are 4 texels per unit, and the batch multiplies texel sizes by it. With `Scale` itself, 9-slice borders and tile steps come out four times too big. The batch comment said `Scale`; it is corrected.
- **`UiBatch.TextScale` is `max(1, floor(Scale / 2))`, not `Scale`.** WS3 and WS4 lay text out in baked pixels (13 and 26) and WS3 already picks Large at scale 3. Scaling every glyph by `Scale` would overflow those layouts. Scale 4 was not previewed.
- **`IClassicHudBackend.BeginFrame` takes `in UiLayout`**, so one value carries the viewport and both scales.
- **`Render.BuildClassicHud` is gone.** `ClassicHudSurface.Draw` is the only caller of `ClassicHud.Build`.
- **The required sprite list grew** by the names asked for plus `exp.tick`, `iconbtn.plinth`, `skill.bracket`, `skill.tile.disabled`, `skill.icon.default` and `qwe.notch`, which the panels also use. All exist in the real files. The `hover` and `pressed` frames of `qwe.slot.*` are not listed, as nothing draws them.
- **`DREAM_HUD_CAPTURE_SETTLE` is new**, because the default 20 frames gave the perf log no line from each state.

## Bugs found while connecting the workstreams

- A status change (a health tick) cancelled any drag, because `HudViewBuilder.Refresh` reports both kinds of change. `InventoryChanged` now separates them.
- `CancelDrag` kept the press, so a held button restarted the drag on the new snapshot.
- Over the ImGui Build panel in classic inventory no cursor was drawn at all. ImGui draws its own there now.
- One long frame could use up the 5 s loading timeout. The step is capped at 0.1 s.
- The character window crown is 40 u but its sides started at 41 u, which showed the world through a gap. Found in the first GL capture.

## Router and modal

The router behaves as WS4 assumes: `BeginDrag` true once, `TryEndDrag` true once, widgets after `ModalActive` is set answer. The "modal one frame deferred" note and `ModalFresh` do not conflict: the note covers widgets built before the modal in later frames, `ModalFresh` covers the frame that opens it. After a modal closes, widgets are dead for one frame, which is harmless. `ClassicHudIntegrationTests` pins this with the real router under the real panels.

## Test support

There was no name clash at compile time: `NullUiInput`, `BlockGlyphs` and `RealFont` each exist once, and other fakes are nested or prefixed. `RealFont` now takes `ImGuiTestGate.Gate`. It is the only test code that makes an ImGui context current today.

## Verification

- `dotnet build dream/dream.csproj`: 0 errors.
- Full suite, 2 runs. Run 1: 5167 passed, 1 failed, 46 skipped of 5214. `StatusBarPanelTests.Build_AllocatesNothingAfterWarmUp` read 7200 B in a single measuring window. Six panel tests moved to `AllocationAssert`. Run 2: 5168 passed, 0 failed, 46 skipped of 5214.
- Compositor previews read at 1080p and 720p: play, inventory, tooltip, dead. Tiers, icons, digits and text are right. They were read before the crown fix.
- First real GL run on the integrated loopback server, 1440x1024, Release: shaders compiled, exit 0, no `render-fault` line, four captures correct.
- Fallback proof with `ui/hud/hud.png` renamed: ImGui surface, exactly one `[render-fault] [hud-classic]` line, exit 0.

Perf at 1440x1024, `DREAM_MAX_FPS=0`, an empty starter scene, only 1 or 2 usable perf lines per state, so these are spot readings, not a baseline:

| State | Frame | HUD CPU | Total draws | HUD commands | HUD quads |
|---|---|---|---|---|---|
| Classic, play | 0.33 ms | 0.05 ms | 19 and 22 | 5 | 127 |
| Classic, inventory | 0.38 ms | 0.10 ms | 41 | 25 | 522 |
| ImGui, play | 0.24 ms | 0.06 ms | 19 | | |
| ImGui, inventory | 0.22 ms | 0.07 ms | 21 | | |

## Open items

- **Draw gate missed.** Inventory is 25 HUD commands against a budget of 16 with windows and tooltip. Text, window art and item pages alternate in draw order. Play is 5 against 12.
- Plan gates not measured: the 1080p FPS baseline on a real scene, allocation in the real frame, atlas load time, GPU memory.
- Mouse hover, drag, right-click and the modals were not exercised in the GL build. The real router and panels cover them in tests only. The capture script drives state, not the mouse. The sprite cursor did follow the real pointer in the captures.
- Combat numbers, crit colours, death and tier icons were not seen in GL: the captured scene had no fight and common items only. They are seen in the compositor only.
- The mode label ("Building", "Combat") sits on the EXP trough.
- Text is 13 px at 1080p and small against the art, as the plan accepted. `BodyTextPx` centring is off by a few pixels at scale 4.
- `NetworkDiagnosticsRateLimiterTests.ShouldLog_OnAnExistingKey_DoesNotAllocate` failed 3 of 3 under the filter `FullyQualifiedName~Allocat`, and passes alone and in both full runs. It uses a single measuring window. This work did not touch it or its code.
- Hiding the developer sliders will surprise a user until `admin_hud=1` is set.
- A new `DREAM_IDENTITY_FILE` on an existing database with the same client name is refused as "That character belongs to someone else", and the window stays on the connection screen. That is correct behaviour, but it stops a capture run.

# Mouse wiring pass, 2026-11-01

Brief: the classic windows had their pieces (router, panels, requests, server intents) but nobody had put a hand on them in the GL build. Base e3d6249, branch `worktree-agent-a7734485a0fa1450a`.

## Plan: the path, traced

Real mouse: `Game.OnRenderFrame` passes `MouseState` to `PlayScene.Render`, then `Render.Hud.DrawFrame`, then `Render.DrawHUD`, then `DrawClassicHud`, which builds a `UiPointer` (window position, both buttons, `Interactive` = focused, inventory open, Settings closed, ImGui not capturing). `ClassicHudSurface.Draw` refreshes the `HudView` (`HudViewBuilder`), runs `UiInputRouter.BeginFrame`, then `ClassicHud.Build`: character window, inventory window, bar, notices, item overlays (tooltip, menu, confirm, ghost), cursor. Panels answer through `IHudActions`; the surface keeps one action and a close flag. `DrawHUD` puts them in the `HudInteractionRequest`, and `PlayScene.ApplyHudRequest` turns an action into a `PlayerActionMessage` (`ToInventoryIntent`) on the remote client queue. The server answers with an `InventoryChangedResult` or a rejection; `ApplyInventorySnapshot` sets `_inventory` and the combatant's equipment, and the next frame the builder refills the view, so the stat rows follow. The cursor: `Game.OnUpdateFrame` reads `PlayScene.DesiredCursorState`.

What was broken or unplugged:

1. **The capture path had no pointer.** Nothing exercised hover, drag or menus in GL. Now a scripted pointer feeds the same router (below).
2. **A fresh snapshot always looked like a change.** `PlayerInventorySnapshot.Equals` compares the lists by reference and every decoded snapshot has new lists, so any push (a loot notice, a result for an unrelated action) cancelled a drag. `HudViewBuilder` now compares contents and keeps the old object when they match, so the panels' own reference check agrees.
3. **Opening V showed two cursors for a frame**: the "classic cursor is active" flag was set after the draw, so the first frame had the ImGui cursor, the sprite and the OS cursor. `Render.ClassicHudCursorActive` is now predicted from the surface state, for both `DesiredCursorState` and the ImGui cursor.
4. **Mouse look after closing.** The OS moves or re-centres the cursor when the mode changes at the end of the closing Update, so the delta of the next frames is the jump. `GameplayInputResumeFilter` covers one frame. `CursorChangeLookHoldoff` zeroes look for three frames after input resumes. Opening needed nothing: gameplay input is already off.
5. **Esc with the inventory open opened Settings.** Now it closes the inventory; from play it opens Settings as before. V is gated on nothing but Settings.
6. **Draw commands**: 25 in inventory. Panels drew art, text, art, text. They now draw all window art, then the item icons one page per tier, then all text, with text cut to fit instead of clipped (a clip splits the command). The bar draws all art, then all text.
7. **Mode label** sat on the EXP trough. It now stands on top of the bar, centred over the skill tile.
8. **Item menu** opened under the gauntlet cursor and hid its first row. It now opens 18 u to the right.
9. **Notices ran over the windows** (the rejection line crossed the stat rows) and the death dim covered the windows, though inventory works while dead. With the inventory open, notices and the death title centre in the strip left of the windows and the dim is not drawn.

Not changed, because it was already right: pointer and layout share one pixel space (client size equals framebuffer size here; the scripted run at 1100x700 hit what it aimed at); the drag lifecycle, the drop rules, the menus and the confirm; a rejected action leaves the view as it was, so the item is already back (nothing moves optimistically); dead players: the server allows inventory actions and the client does not gate V.

## Scripted pointer

`DREAM_HUD_CAPTURE_SCRIPT` takes pointer steps (see `docs/configuration.md`). `Render.ScriptedPointer` is null unless a script runs; `DrawClassicHud` then uses it in place of the mouse (focus is ignored). The steps are built by `HudCapturePointerPlan` (pure, tested) and played by `PlayScene.Capture.cs`, which waits for the server's answer (snapshot applied, rejection shown, or a confirm opened; 240 frames at most), logs the inventory and stat rows before and after, and writes the PNG. `InventoryUiState` now records the menu row and confirm button rects the last frame drew, so a script can click them.

## Real-game run

Release build, copied to a scratch folder with its own `config/player-profiles.db` (throwaway), `DREAM_AUTO_CONNECT=1`, no `DREAM_SERVER_HOST` (integrated loopback), own `DREAM_IDENTITY_FILE`. One join to create the character, then `--items grant` for six rolled items (two Uncommon and one Rare blade or lantern, two caps, a Rare vest), which the HUD read back with rolled stats.

Start (slot=item, `*` equipped alias): 0 builder hammer*, 1 field blade, 2 stone maul, 3 survey lantern, 4 torch, 5 iron blade Uncommon (Str +6, AtkSpd +1.35), 6 iron blade Rare (Str +9, AtkSpd +2), 7 ward lantern Uncommon (Armor +4), 8 leather cap Rare (Armor +6, Vit +2), 9 leather cap Uncommon, 10 leather vest Rare. Worn: hammer. Stats: Strength 12 (10 +2), Attack Speed 2.25, Vitality 10, Armor 0.

| Step | Intent that reached the server | Result in the next snapshot |
|---|---|---|
| `drag:10:13` | Move 10 to 13 | vest at 13 |
| `drag:6:5` (occupied) | Move 6 to 5 | the two blades swapped |
| `drag:5:main` | EquipTo 5 into 100 | Rare blade worn, hammer unaliased; Strength 19 (10 +9), Attack Speed 4 |
| `dragging:8:main`, `release` | none: invalid well (red overlay), nothing sent, no answer | unchanged |
| `drag:8:head` | EquipTo 8 into 102 | cap worn; Vitality 12, Armor 6, HP 110 |
| `drag:head:15` | Unequip 102 | Vitality 10, Armor 0 |
| `rightclick:7`, `menu:0` | EquipTo 7 into 101 | lantern worn; Armor 4 |
| `rightclick:4`, `menu:2`, `confirm:cancel` | none | torch kept |
| `rightclick:4`, `menu:2`, `confirm:destroy` | Destroy slot 4, item 6 | torch gone |
| `drag:main:11` | Unequip 100 | Strength 10, Attack Speed 2 |
| `burst:5:6:100`, `DREAM_MAX_FPS=0` | 100 moves | 17 accepted, the rest rejected: "Inventory move rejected because too many inventory actions, try again shortly." shown in red; the view matched the server |
| `v`, `esc`, `close`, `v` | none | cursor Hidden when open, Grabbed when closed; Esc closed the inventory and left Settings shut |
| `size:1100:700` then `drag:6:main` | EquipTo 6 into 100 | works at 1100x700 (UI scale 1.25) |
| `ui_scale=3` then `drag:8:head` | EquipTo 8 into 102 | works at scale 3 |

Draw commands in the GL build (`[hud-capture]` lines): play 2 (was 5), inventory 10 (was 25), with a tooltip 12, with the item menu 12, with the destroy confirm 16. Headless worst case with all three tiers on screen and a tooltip: 12 (was 33).

## Left for a human hand

- Real mouse feel: the 4 px drag threshold, the cursor leaving the window while dragging (Hidden mode lets it), and the mouse-look jump after V or Esc on the user's driver (the hold-off is three frames; it was reasoned from GLFW behaviour, not measured).
- A configured `ui_scale` above the auto fit for the window (3 at 1440x1024) pushes the lower windows under the bar. The pointer is still right; the layout was left alone.
- Death with the inventory open was not seen in GL; the headless panels and the server rule cover it.
