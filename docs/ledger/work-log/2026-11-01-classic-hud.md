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
- Death with the inventory open was not seen in GL; the headless panels and the server rule cover it.

# Verification pass, 2026-11-01

Tip under test: 2226a07 (mouse merge on fb3d532). Machine otherwise idle. Release build, integrated loopback route (`DREAM_AUTO_CONNECT=1`, no `DREAM_SERVER_HOST`), throwaway database and `DREAM_IDENTITY_FILE` in a copied build folder per variant, `DREAM_RENDER_PERF_LOG=1`, `DREAM_MAX_FPS=0`, window 1920x1080 client (`size:1920:1080` as the script's first step; the baseline build had its two `Vector2i(1440, 1024)` literals in `Game.cs` changed to 1920x1080 in a throwaway detached worktree, which is not committed). Baseline: master 1aee5f0. Four rounds, each round one run of every variant in the same order (base, classic play, ImGui play, classic inventory, ImGui inventory), about 25 s each. The scene is the starter world after the player joins, with no enemy near.

## Measured gates

FPS is the window title's own FPS counter, sampled once a second after the first steady samples, then the mean of each run; "range" is the min and max of the four run means. HUD CPU and GPU HUD are the `HUD:` and `GPU: hud` fields of the perf line (two decimals, so 0.01 ms steps). The frame is so cheap (0.2 ms) that 3% is 0.006 ms: read the FPS ratio as "no regression visible at this resolution", not as a precise cost.

| Gate | Budget | Measured | Result |
|---|---|---|---|
| FPS, play, classic vs master | at least 95% | 4857 vs 5006 (run means 4830 to 4873 vs 4884 to 5140) = 97.0% | pass |
| FPS, play, ImGui on the tip vs master | no regression elsewhere | 5059 vs 5006 | pass |
| FPS, inventory open, classic vs ImGui on the tip | at least 95% | 4581 vs 4544 = 100.8% | pass |
| HUD CPU, median (max) | at most baseline + 0.5 ms, under 1.5 | master 0.02 (0.12); classic play 0.03 (0.16); classic inventory 0.04 (0.10); ImGui inventory 0.04 (0.13) | pass |
| GPU HUD, median (max) | under 1.0 ms | 0.00 (0.00) play, 0.01 (0.01) inventory | pass |
| Total draws in the frame | not a gate | play 19 on all three, inventory 24 classic vs 21 ImGui | note |
| HUD draw commands | at most 12 in play, 16 with windows and tooltip | play 2 to 4, inventory 8 to 10, with tooltip 12, inventory with all three tiers 11 to 13, destroy confirm 16 (mouse pass) | pass, confirm at the limit |
| Atlas, first HUD frame to Ready | decode off-thread under 800 ms | 878, 814, 768, 752 ms | 3 of 4 under; see note |
| Atlas, longest HUD `Update` | one atlas per frame, longest frame under 50 ms | 0.4 to 0.7 ms for six of seven pages; the first upload 22.6 ms in one run and 3.1 to 3.9 ms in the other three; longest frame while uploading 29.8 ms | pass |
| Managed allocation per frame | 0 B | 0 B for `ClassicHudSurface.Draw` with the real batch, builder and router in play, inventory, and inventory with a tooltip (`ClassicHudAdversarialTests.WholeSurfaceFrame_*`, `AllocationAssert`) | pass, not measured in the game process |
| Build output | +7.65 MB, limit 8.5 MB | +7,834,197 B (7.83 MB) over master, of which `dream.dll` and `.pdb` are 0.19 MB | pass |
| GPU memory | about 50 MB, 65 MB with mips | not measured (no GL memory query was available) | not verified |

Atlas note: the time from the first HUD frame to Ready includes one scene stall of 700 to 740 ms that the HUD does not cause (the first `Update` ran at 2 ms, the next at 704 to 760 ms, with a frame delta capped at 100 ms). The pages were already decoded when that second update ran (it uploaded one), so the decode finished inside the stall, but the decode time alone was not separated. 7 updates, 7 pages, one per frame, as designed.

Over 1.19 MB of the build growth is `items_enhanced.png` and `items_mask.png`, which `UiAtlasStore.FilePages` does not load. (Fixed in fix loop 1: no longer copied.)

## Goldens and byte pins

`git diff master -- dream/Simulation dream/Server dream/Multiplayer` is four files: `DamageEventDelta.cs` (a trailing `HitKind`, `ToString` unchanged), `WorldSessionRuntime.Replication.cs` (passes `evt.Kind`), `ServerSnapshotJsonConverters.cs` (reads and writes `hit`, only when not Regular) and `TransportEnvelope.cs` (11 to 12). Nothing in `dream/Server`. The golden tests are unchanged against master; `WorldSessionRuntimeTickEquivalenceTests.ScriptedRun_ProducesTheSameSnapshotsResultsAndTargetOrderAsBeforeMem3` (`GoldenCheckpoints`), `AuthoritativeCombatResolverTests.RuntimePath_SeededRolls_MatchPreExtractionGoldenAndTheResolver` (`PreExtractionGolden`) and `MapSourceGoldenTests` pass. In the protocol pin tests only the version digit moved (read line by line).

## Scenes seen in the GL build (1920x1080, `DREAM_TIME_OF_DAY=midnight`, `DREAM_MAX_FPS=60`)

This needed a new script step, `fight:<frames>` (see `docs/configuration.md`): the nearest live enemy is aimed at with look deltas, approached, and attacked in combat mode through the normal command frame.

- Regular damage: orange numbers rising above the wolf, fill and dark rim as specified.
- Critical: a blue 87 above the Dire Wolf.
- Incoming: red numbers appear in third person (an 18 over the player). In first person the player went from 100 to 0 over a dozen captures and no red number was drawn. The cause was not instrumented: the player's combat anchor is at the camera, and `TryProjectWorldToScreen` drops a point at or behind the near plane. Whether the ImGui HUD showed them in first person was not established. (Fixed in fix loop 1.)
- Death: dimmed screen, red "You died" and "Respawning in N s" at 38% height, empty gem, `0/100`. With the inventory open: no dim, the text centred in the strip left of the windows (the cursor can sit on it), windows live. Respawn with the inventory open: 100/100 and the windows unchanged.
- Rare and uncommon icons and tooltips: pink uncommon and mint rare names with the tier label, correct rolled stat lines (`+8 Strength`, `+1.85 Attack Speed`, `+7 Strength`, `+1.4 Attack Speed`), item icons per tier.
- Loot notice: a real drop, pink "Looted: Ward Lantern (Uncommon)" over the red cooldown rejection line.
- Dire Wolf label at night: present ("Dire Wolf", "Lv 3  200/200"). When a Wolf and a Dire Wolf stand together their labels overlap into unreadable text, since each is centred on its own anchor with no avoidance. (Fixed in fix loop 1.)
- Drag while the server rejects: ghost icon, dimmed source slot, the drop well tinted invalid, the red "too many inventory actions" line in the left strip, view unchanged afterwards.
- Window 1100x700: the layout adapts; the "Attack Speed" label touches its value ("Attack Speed2 +0.25 = 2.25"). (Fixed in fix loop 1.)

## Adversarial

- Atlas faults, each in a copy of the build at 1920x1080: truncated `hud.png` ("CRC Error. PNG IDAT chunk is corrupt!"), `hud.json` as `{ not json`, `cursors.png` replaced by `hud.png` ("Cursors: JSON size does not match the 1692x1452 image."), `exp.tick` removed from `hud.json` ("Required UI sprite 'exp.tick' is missing."). Each printed exactly one `[render-fault] [hud-classic]` line, drew the ImGui surface for the whole run, and closed normally.
- F3 by real `WM_KEYDOWN` messages posted to our own window: with `DREAM_ADMIN_HUD=1`, Classic, ImGui, Classic, ImGui after three presses; with 0, ten captures all Classic.
- Minimise (1.5 s and 4 s) and restore, then two resizes, with the inventory open and a tooltip up: no fault; the minimised frames draw 4 quads, the restored frame is the full 805 again, and the layout re-fits at 1100x700 and 1500x900. The scripted pointer ignores focus, so the focus-loss rules were exercised in tests only (`WindowLostMidDrag_*`, `WindowLostWithAMenuOpen_*`).
- Not reachable from GL: world transfer with the inventory open, an item removed under the pointer, a hostile item name, an unknown kind and the 8192 quad limit. The first two need a server event, the next two need content the server controls, and no frame reached 900 quads. They are in `ClassicHudAdversarialTests` (23 tests).
- Protocol: `OldClient_IsRejectedWithALoggedReason_*` now also runs a v11 client against the v12 server ("Client uses protocol v11; this server uses v12."), and the client side `ProtocolVersionMismatchClientTests` pass.

## Tests added

`dream.Tests/Rendering/Ui/ClassicHudAdversarialTests.cs` (23): hostile names (`%s {0}`, 20,000 characters, 4,000 percents, control and bidi characters, a lone surrogate, empty) through hover, menu and destroy confirm with the real `UiBatch`; loot names; unknown kind; item removed mid-hover and mid-drag; resize during a drag (and to 1x1, 160x90, 7680x4320); rejection while dragging; window lost mid-drag and with a menu open; world transfer with a menu or a drag; the 8192 quad limit with a full batch, 16 long item names, 20 long loot notices and a 5,000 character rejection; whole-surface allocation. `HudCapturePointerPlanTests` gains `fight` grammar cases; `ProtocolV2VersionRejectionLoopbackIntegrationTests` gains the v11 case.

Production code changed, capture script only: `HudCapturePointerPlan.cs` (the `Fight` verb), `PlayScene.Capture.cs` (`ApplyCaptureFight`), and one call in `PlayScene.Update.cs`, which returns the frame untouched unless a fight step is running.

Full suite: run 1 of 2, 5257 passed, 0 failed, 46 skipped of 5303, 2 m 39 s.


# Fix loop 1, 2026-11-01

Base 0a50211 (tip of `integrate/classic-hud`), branch `worktree-agent-a60d3d1ff2cc18264`. Simulation and server code are untouched; the tick goldens and byte pins did not move (the full suite passes them unchanged).

## What was wrong and what changed

1. **Killing blow showed no number.** `PlayScene.Replication.cs` applied the actor snapshot (which removes the dead enemy from the client registry) before `ClientSnapshotDamageApplier`, so `TryResolveCombatActor` failed for the last hit. Damage now runs first and still runs before the presenter. `CombatTextLoopbackTests.ALethalHit_PublishesExactlyOneDamageAppliedEventOnTheClient` (real loopback server, real `PlayScene`) sets the enemy to 1 HP, lands one hit and expects exactly one event. With the two lines swapped back it fails (`Assert.Single() Failure: The collection did not contain any matching items`).
2. **`ui_scale` above the auto fit.** `UiScale.Resolve` now returns `min(snapped configured, auto fit)`; without a viewport (0 by 0) it only snaps, which `HudOptions.SanitizeUiScale` relies on. The Settings slider shows the scale in use. Auto is unchanged. `UiScaleTests.Configured_SnapsToQuarterAndIsCappedAtTheAutoFit` changed its last case (9 at 1920x1080 is now 2.25, not 4), and `HudOptionsTests.UiScalePick_IsSavedWithTheRuntimeSettings` now expects the resolved 2.0 at 1440x1024 while the saved value stays 2.25. Both are the intended change of meaning.
3. **4K text.** `IUiCanvas.TextScale` (default 1; `UiBatch` already had it from `UiLayout.TextScale`); `PanelText.Height(c, style)` and `InventoryWindow.BodyHeight(c)` scale the baked 13 and 26 px by it, and the status bar, notices, labels, inventory, character window and item overlays use them. Previews written by `HudPreviewTests.RealPanels_HighScale_PlayAndInventory` (3840x2160 and 2560x1440) were read: at 4K the text is doubled and sits inside its rows, tooltip, bar and gem; at scale 3 (2560x1440) the HP text is Large and fits the gem, and coordinates and EXP percent are centred.
4. **GL failure did not latch.** `UiGlTextureUploader` checks `GLUtils.CheckGLError` after the upload; on any result it deletes the texture and throws, so the atlas store ends in Failed. `UiGlRenderer` throws from its constructor on a bad result and checks once, after the first frame that actually draws (stale errors are drained first so they are not blamed on it); then it never checks again, so there is no per-frame `glGetError`. `GL_OUT_OF_MEMORY` and `GL_CONTEXT_LOST` stay latched Fatal by the existing `CheckGLError` rule and close the window instead of falling back; `docs/configuration.md` says so. Not unit-tested: it needs a GL context.
5. **Glyph guard.** `UiFontSetup.BuildGlyphTable` also fails (so the HUD falls back) on a Dear ImGui version other than the audited `1.91.6`, and when the `?` glyph of either style has X1 not above X0, UVs outside 0..1 or an advance not above 0 (`UiFontSetup.GlyphFault`, tested directly; the real font and the loaded native version pass).
6. **Test gaps.** The cursor decision is `ClassicCursorPolicy.Decide` and `WillDraw` (table test: inventory, settings, loading, failed, latched/F3/forced, unfocused). `ClassicHudFrameGuard.Draw` is the frame run `Render` now uses: on a throw it drops the pending action and close, calls the fault handler (which latches ImGui once) and reports nothing drawn; tested with a backend that throws after an action was already picked. `DamageEventHitWireTests.ToString_IsPinnedAndNeverPrintsTheHitKind` pins the `DamageEventDelta` text.
7. **Incoming number in first person.** Reproduced in GL before the fix (see the verification pass above). Cause as suspected: the player's combat anchor is at the camera, so the projection drops it. The ImGui HUD on master projected the same anchor (plus a depth offset toward the camera) through the same `TryProjectWorldToScreen`, so by reading the code it had the same gap; it was not run on master. Fix: `IncomingNumberAnchor` puts a local-player Incoming number that cannot project, or whose anchor is within 1.5 m of the camera, at the horizontal centre and 62% down the window. In third person it still rises over the character. GL capture below.
8. **`InventoryUiState` reset** in `DisposeClassicHud` (only when a classic surface existed, so headless scenes do not touch the static state) and in `Game` when a play scene starts and when a session ends.
9. **Unfocused window keeps an OS cursor**: focus is part of `ClassicCursorPolicy.Decide` (`PlayScene` records it each render).
10. **Tooltip name** wraps by width (two lines at most) instead of being cut by character count; the gap after the name moves to the end of the name block.
11. **Batch drops**: `Render.SubmitClassicHud` logs once per run (`[hud-classic] N quads did not fit ...`). World labels draw first and could starve the bar, so they are capped at 24 and stop once less than 4096 of the 8192 quads remain (`IUiCanvas.QuadHeadroom`).
12. **Row rebuilds split**: `HudViewBuilder` rebuilds stat rows only on a status change and item rows only on an inventory change.
13. **Overlapping enemy labels** move down below the earlier one (stack buffer, no allocation, one pass per placed label).
14. **Stat rows**: a gap of `max(6 px, 3 u)` between label and value; when the `base +bonus = total` form would not leave that gap the row shows the total alone.
15. **Unused PNGs**: `items_enhanced.png` and `items_mask.png` are `CopyToOutputDirectory=Never` in `dream.csproj`; both stay in the repo and in `items.json`. No test expected them in the output.
16. **Docs**: Esc note in `configuration.md` (the inventory closes first, on both surfaces; this was the mouse pass's behaviour change and it is kept), the `GetRarity` comment (an out-of-range index throws), protocol version 12 in `README.md` and `build-and-run.md`, the `ui_scale` cap, the first-person number and the label rules.

## Capture tool

`fight:` could not reach the enemies in a fresh world: they stand behind a wall at about (45, 45) and the player stopped at (11.5, 10.5). The step now jumps and sidesteps when it makes no progress, and after 200 ticks of that it toggles noclip once through the real command path. It logs the position. This is verification-tool code only.

## Verification

GL capture, Release, 1920x1080, `DREAM_TIME_OF_DAY=midnight`, integrated loopback route (`DREAM_AUTO_CONNECT=1`, no `DREAM_SERVER_HOST`), throwaway database and `DREAM_IDENTITY_FILE` in a copied build folder, `DREAM_MAX_FPS=60`, sixteen `fight:400` steps, ended by the timeout after the 16 captures:

- `artifacts/gl-capture/14-fight-400.png`: red `12` and `10` (incoming) in first person, "You died", the Wolf and Dire Wolf labels on separate rows, orange `39` and `46` hits above.
- `artifacts/gl-capture/04-fight-400.png`: death with the loot notice and the two enemy labels one above the other.
- Every capture reported `dropped=0`.
- `artifacts/` is git-ignored; the full capture folder of the run is in the session scratchpad (`cap5`).

Not exercised in GL: the GL error paths (they need a driver that fails), a killing blow's number (covered by the loopback test; the run shows loot notices after kills), the minimised and unfocused window.

## Open items

- The GL failure paths have no automated test.
- Incoming numbers rise from 62% height into the same area as the death title and enemy labels; readable, not pretty.
- The ImGui HUD's first-person behaviour for incoming numbers was inferred from code.
- Draw-command gate, the unmeasured plan gates and the real-mouse feel items from the earlier sections stand.
