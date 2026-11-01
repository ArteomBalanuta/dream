# Plan: classic-MMO HUD and UI asset integration

**Goal:** ship a sprite HUD we own (status bar, inventory and character windows, item icons with tiers, atlas combat numbers) as the default in-game UI on real data. The current ImGui HUD moves behind a display-only admin key and is also the automatic fallback.

Nothing was built or run for this plan. Claims come from reading code and a trial `git merge-tree`.

**Paths:** repo root is `G:\workspace\projects\dream`. Paths in workstreams are repo-relative because each implementer works in its own worktree.

**Standing rules for every implementer**
- Integration branch: `integrate/classic-hud`. Nothing goes to master and nothing is pushed.
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Full-suite runs: at most 2 per verification step. Isolate flakes instead of re-running.
- Tests use an absolute `BaseOutputPath` inside the worktree.
- Worktrees start from master, so reset them to the C0 commit first.
- Out of scope: enemy pathing and wave I fix loop 5.
- Rules for art and names: original art only, and no source-game names in code, sprite names or UI strings.

---

## 1. What exists

### Git and base (verified)
- `integrate/ui-assets` (5c2f193) has merge base **45efecc** with both master and engine-iteration. It is not based on the tip, so the handover was loose on this.
- Everything after 45efecc on the engine side is one work-log file.
- Art branch diff is add-only, with no modified files. `git merge-tree master integrate/ui-assets` is clean (tree e11d4f5).
- Master 1aee5f0 contains all of engine-iteration, so decision 7 is moot: **base on master**.

### Handover warnings

| Claim | Verdict | What it means now |
|---|---|---|
| Mouse grabbed in play | True (`RuntimeState.cs:16`) | Bar is display-only in gameplay. Interaction happens in the existing V overlay. |
| No keyboard to ImGui | True | Only chat needs it, and chat is out of scope. Not a blocker. |
| Bag 1x1 vs multi-cell icons | True; `meta.footprint` is art-only | Resolved by fit-scaling, decision 4. |
| Equipped items alias a bag slot | True (`PlayerInventoryService.cs:31-34`). `IsEquipped` is set at `:623`. A legacy row may have no backpack row. | Keep the alias, decision 3. |
| Crit and type absent on client | True, and worse than stated (below) | Small wire change, WS5. |
| Per-item tint needs renderer change | True for ImGui only | Moot: our batch has its own shader, and static tier PNGs suffice. |
| Missing systems, no licensed font | Taken from 04; I did not re-audit. `dream/fonts/Arial.ttf` is the only TTF (no licence found) and 07 forbids rasterising it for art. | Decisions 5 and 8. |

### New findings
- **The server discards crit.** `WorldSessionRuntime.Replication.cs:322` builds `DamageEventDelta` from `DamageAppliedEvent` and drops `evt.Kind`. This closes 04's "not fully traced" note: the resolver's Critical publish stays on the server hub. Note `DamageEventDelta.Kind` is `CreatureKind`, a naming trap.
- **Probable double numbers.** An attacker's hit publishes `DamageAppliedEvent` from both `ClientSnapshotDamageApplier.cs:44` and `ClientAuthoritativeActionApplier.cs:74`. I found no dedupe. This is from code reading, not observed. WS5 proves it first with a loopback test.
- **Goldens.** `AppendEnvelope` in `WorldSessionRuntimeTickEquivalenceTests.cs` seems not to hash damage events (damage appears only in outcome counters). `result.Payload.ToString()` is hashed, so `CombatResolutionResult` must not change.
- **Firewall.** Only `ServerBootstrap.cs:297` binds `IPAddress.Any`, for `--server`. The integrated server binds loopback (`IntegratedLocalServerHandle.cs:69`).
- **Admin is not a privilege, and already was not.** `PlayerActionHandler.cs:75` accepts `PlayerStatsUpdateIntent` from any client (decision U4). Hiding the sliders is display only. It neither secures nor changes anything on the server.
- **Headless scene trap.** `HeadlessPlayScene` auto-constructs only `Dream.Rendering.Hud` and non-Rendering fields. A `Dream.Rendering.Ui` field on `PlayScene` stays null there, so replication code must not touch new UI fields.

### Stale in the specs
- 04: all M/I distinctions, hashes (d504d4d, ee50e67), "protocol M=8", and "95 ahead". Master now has everything labelled I. Its line citations in `Render.Hud.cs` and `Render.ImGui.cs` still hold where I checked.
- 04: the suggested layer "over ImDrawList" is superseded by our own batch here. `ImDrawList` cannot do the two-channel digits, and the skipped `UserCallback` stops being relevant.
- `CharacterPanelModel.cs:83` comment ("no effect on combat") is stale. WS4 fixes it.
- 00 and 01 still use source-game names (Zen, Chaos Machine). 06 and 07 override where they differ. Sprite names are clean (`BAD_NAME` check, `currency.*`).

### Art usable now
198 sprites: window 54, hud 80, cursors 5, items 28, fx 28, ramps 3.

- All 9 items in `items.json` have sprites, so `item.<ItemKind>` maps directly. Use `ItemKind`, not `VisualId`, because `VisualId` is shared between items (iron blade and field blade).
- Unused this round: 18 extra item sprites, `chat.*`, `combine`, `items_enhanced`, `items_mask`, `tier_ramps`, `fx.aura.blob`, `dmg_digits` (x1), `dmg.miss`.
- JSON schema: `x,y,w,h` exclude extrusion, `pivot` px, `slice` `[l,t,r,b]` px or null, `tile` none/x/y, `meta`.
- Previews look consistent by eye.

---

## 2. Decisions

| # | Topic | Decision | Reason |
|---|---|---|---|
| 1 | Camera fit | No click mode. In gameplay the HUD is display-only and the mouse stays grabbed. V is the existing overlay and frees the cursor. Reuse `InventoryOverlay`. | Ships soonest, no new mode. A held-key mode can be added later because widgets already use a router. |
| 2 | Paper doll | Four real wells: MainHand `well.2x3` + `sil.weapon.left`, OffHand `well.2x3` + `sil.weapon.right`, Head `well.2x2` + `sil.helm`, Body `well.2x3` + `sil.armor`. Other wells hidden. Rects from 01 section 3.8. | A later slot is one table row. |
| 3 | Equipped items | Keep the server alias. Item shows in its well and in the bag at 55% tint with `cell.overlay.hover`. Legacy item with no bag row shows in its well only. | No server rule change. |
| 4 | Multi-cell bag | Grid stays 4x4. Each slot is a 2x2 block of 20 u cells, 40 u, filling the art's 160x160 u grid (1 dream slot = 4 `cell.*`). Icons are fit-scaled by footprint into 40 u and centred. | No server, wire or migration work. Footprints stay in data. |
| 5 | Missing systems | Hidden, or an empty art socket with no number, hover or action. See section 4. | Bar stays symmetrical without faking data. |
| 6 | Tier mapping | Common `#F9F8FA` + `items.png`; Uncommon rose `#F08FB0` + `items_uncommon.png`; Rare mint `#5FD6A0` + `items_rare.png`. Enhanced unused (no source data). ImGui colours unchanged. | Per 02 and finding 17. |
| 7 | Base | `integrate/classic-hud` from master 1aee5f0, with `integrate/ui-assets` merged. | Verified clean. |
| 8 | Font | ImGui's built-in default font (ProggyClean; ImGui's MIT licence, per my recollection, confirm when picking), baked at 13 and 26 px, glyphs drawn through our batch. Arial not used. One swap point: `UiFontSetup`. | Only option with no provenance question. Licensed font is a later one-line change. |
| 9 | Crit | Protocol **v12**: one trailing, omit-when-default field. See WS5. | Crit is the headline of spec 05 and cannot be derived reliably (`amount > MaxDamage` fails with armour and for other players). Precedent: v10 trailing fields. Merged last and droppable: without it every number is Regular. |
| 10 | Damage type | No wire change. No such concept exists in simulation. Incoming is derived on the client from actor id. | Nothing to send. |
| 11 | UI scale | `min(H/480, W/640)` floored to 0.25, clamped 1 to 4. `ui_scale=0` is auto; other values snap to 0.25. 1080p 2.25, 720p 1.5, default 1440x1024 2.0. | Floor guarantees 432 + 48 u fits. |
| 12 | Text pipeline | All text in our batch, not ImGui, so tooltips occlude text underneath. | ImGui text would float above sprite tooltips. |
| 13 | Blend | One premultiplying shader, `GL_ONE, GL_ONE_MINUS_SRC_ALPHA`. | Spec 05. Straight-alpha art is premultiplied in the fragment shader. |
| 14 | Filtering | LinearMipmapLinear, `TEXTURE_MAX_LEVEL=2`, ClampToEdge. | 4 px extrusion survives two levels; 720p samples at 0.375. |
| 15 | Gem and gauge fill | UV-clipped quad over the empty layer. Gem mask unused. | Mask is monotonic with y, so a clip is equivalent and needs no extra sampler. |
| 16 | Still ImGui | Settings window, Build material panel, startup menu. | Parity with no new widgets. Reskin later. |
| 17 | Character window | `win.crown.combine` + `win.slab.combine` with stat rows, left of the inventory; no combine UI. V opens both. | C is camera, and the art has no stat panel. |
| 18 | Spec pack | Commit the 8 `.md` files. Do **not** commit `reference/` (third-party video frames, 2.3 MB); it stays untracked on disk. | Implementers do not need them, and generators are barred from them. |
| 19 | Combat motion | Classic curve only for the sprite path: starts large, shrinks, newest on top, 1.4 s base lifetime, tunable. Cap 12 per target. Floor 24 px, spawn 68 px of fill at 1080p, scaled by screen height and by `SizeMultiplier / 2.35`, so existing tuning stays meaningful. | 06 finding 16. |

---

## 3. HUD system design

New namespace `Dream.Rendering.Ui` is GL-free. `Dream.Rendering.Ui.Gl` is the only part touching OpenTK.Graphics. A source-guard test enforces this.

**Frame flow, classic surface** (`Render.DrawHUD`):
1. Crosshair.
2. `BeginHudFrame`.
3. Build: CPU only, fills the vertex array, no GL.
4. Submit: one `BufferSubData`, draws, state restored.
5. ImGui windows (Settings, Build panel) on top.

Exceptions thrown in build happen before any GL call.

**Layers**
- **Atlas:** `UiSpriteTable` (JSON to `UiSprite`), `UiAtlasStore`. Decode on the thread pool via `BackgroundWork`, upload one atlas per frame on the render thread. States: Loading, Ready, Failed.
- **Batch:** `UiBatch : IUiCanvas` with preallocated arrays (8192 quads) and static EBO. Vertex: pos, uv, tint RGBA8, rim RGBA8, mode byte. Command list merges by texture and scissor. Mode 0 colour, 1 two-channel mask (`rgb = (r*fill + g*rim)*fade`).
- **Layout:** `UiLayout` from viewport and scale. Bar groups left, centre, right plus `hud.gapfill` tiled x. Inventory top-right, character to its left. Bar draws above windows.
- **Input:** `UiInputRouter`, immediate-mode. Hot id is the last hit from the previous frame. Modal layer for menu and confirm.
- **View model:** `HudViewBuilder` refills a reused `HudView`. It rebuilds rows only when status or the inventory snapshot changes.

**Input routing**
- UI is interactive only when `InventoryVisible && !SettingsVisible && !io.WantCaptureMouse`.
- Gameplay input is already off in overlays, and `GameplayInputResumeFilter` already swallows a held click on close, so that filter is unchanged.
- In classic inventory, `DesiredCursorState` is Hidden with the `cursor.*` sprite and `io.MouseDrawCursor=false`. Settings keeps today's behaviour.

**Admin key: F3**
- No F-keys are bound (`Keys.F` is freeze). F12 is the debugger break on Windows; F10 is the system-menu key.
- Handled in `HandleGlobalInput`, no-op unless available. In-session only, not persisted, and it cancels any drag.
- Config `admin_hud=0|1`, default 0. Env `DREAM_ADMIN_HUD=1|0` overrides for a run and is never saved (same pattern as `FrameRateCap`).
- Env `DREAM_HUD=imgui` forces the ImGui surface for baseline comparison.
- New HUD is default on.
- Perf harness and tooling do not depend on the HUD. `[render-perf]` is console text from `performance.ToHudLines()`, so it survives.

**Dev controls in Settings:** when not admin-available, hide the stat sliders, Fly Speed, collision, perf counters, world size, viewmodel and time of day. Keep FOV, sensitivity, third person, damage text, resolution, connection info, and new UI scale. Handoff must tell the user to set `admin_hud=1` to get these back.

**Not a privilege**
- Nothing about the setting goes on the wire or reaches the server.
- F3 emits no `HudInteractionRequest` fields and no traffic.
- ImGui shows only data the client already has.
- Test: token `AdminHud` is absent from `dream/Server`, `dream/Simulation`, `dream/Multiplayer`.

**Fallback.** `HudSurfaceSelector` (pure):
- Atlas or shader failure latches ImGui with one `RenderFaultLog.Shared.Report("hud-classic", ...)`, regardless of `admin_hud`. F3 is then inert.
- Triggers: missing file, corrupt PNG, bad JSON, JSON size not matching PNG, out-of-bounds sprite, missing required sprite, GL failure, exception in build or submit, or Loading longer than 5 s.
- Dev controls stay gated in fallback.
- During Loading the HUD draws only the crosshair.

### C0 contract (load-bearing)
```csharp
namespace Dream.Rendering.Ui;
public enum UiPage : byte { Window, Hud, Cursors, ItemsCommon, ItemsUncommon, ItemsRare, Digits, Font }
public enum UiTile : byte { None, X, Y }
public readonly record struct UiColor(byte R, byte G, byte B, byte A);
public readonly record struct UiRect(float X, float Y, float W, float H) { public bool Contains(Vector2 p); }
public readonly record struct UiSprite(UiPage Page, int X, int Y, int W, int H, int PivotX, int PivotY,
    int SliceL, int SliceT, int SliceR, int SliceB, UiTile Tile, int FootprintCols, int FootprintRows);
public enum UiTextStyle : byte { Body, Large }

public interface IUiCanvas {
    void Sprite(in UiSprite s, UiRect dst, UiColor tint);
    void SpriteClipped(in UiSprite s, UiRect dst, UiRect srcNormalized, UiColor tint);
    void NineSlice(in UiSprite s, UiRect dst, UiColor tint);
    void Tile(in UiSprite s, UiRect dst, UiColor tint);
    void Solid(UiRect dst, UiColor color);
    void MaskGlyph(in UiSprite s, UiRect dst, UiColor fill, UiColor rim);
    float Text(ReadOnlySpan<char> text, Vector2 pos, UiTextStyle style, UiColor color);
    float MeasureText(ReadOnlySpan<char> text, UiTextStyle style);
    void PushClip(UiRect r); void PopClip();
}
public readonly record struct UiPointer(Vector2 Position, bool LeftDown, bool RightDown, bool Interactive);
public interface IUiInput {
    bool Button(int id, UiRect r);           // click on release
    bool Hovered(int id, UiRect r);
    bool RightClicked(int id, UiRect r);
    bool BeginDrag(int id, UiRect r, int payload);
    bool TryEndDrag(out int payload, out Vector2 dropPosition);
    int ActiveDragPayload { get; }           // -1 none
    bool ModalActive { get; set; }
}
public static class UiScale { public static float Resolve(int width, int height, float configured); }
public interface IHudActions { void Inventory(InventoryHudAction action); void CloseInventory(); }

public sealed class HudView {   // filled by HudViewBuilder, read-only to panels
    public HudStatusSnapshot Status { get; }
    public PlayerInventorySnapshot? Inventory { get; }
    public CharacterSlotRow[] Equipment { get; }
    public CharacterStatRow[] StatRows { get; }
    public HudItemView[] Backpack { get; }       // 16
    public HudItemView[] Worn { get; }           // 4
    public Vector3 PlayerPosition { get; }
    public string? RejectionMessage { get; }
    public WorldTransferHudState WorldTransfer { get; }
    public LootFeedbackSystem? Loot { get; }
    public IReadOnlyList<FloatingCombatTextRenderState> CombatText { get; }
    public IReadOnlyList<HudWorldLabel> Labels { get; }
    public bool InventoryOpen { get; }
}
public readonly record struct HudItemView(InventoryItemSnapshot Item, bool HasIcon, UiSprite Icon, ItemTier Tier, bool Equipped);
public enum ItemTier : byte { Common, Uncommon, Rare }
public readonly record struct HudWorldLabel(Vector2 Screen, string Name, string Health);

public static class ClassicHud {   // frozen after C0: call order only
    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s, IHudActions a) {
        WorldLabelsPanel.Build(c, v, l); CombatTextPanel.Build(c, v, l, s);
        CharacterWindow.Build(c, i, v, l, s, a); InventoryWindow.Build(c, i, v, l, s, a);
        StatusBarPanel.Build(c, i, v, l, s, a); NoticePanel.Build(c, v, l);
        ItemOverlays.Build(c, i, v, l, s, a);   // tooltip, context menu, confirm, drag ghost
        CursorPanel.Build(c, i, v, l, s);
    }
}
```
All text APIs take spans and never treat input as format strings. Server strings are clipped by width. Glyphs outside Latin draw `?`.

---

## 4. Scope this round

**Real data**
- HP gem fill and cur/max text.
- EXP trough and percent; level in `exp.cup`.
- Mode (Combat or Building) on `skill.tile`.
- Coordinates on `hud.coordsplate`.
- Satchel icon: Close when an overlay is open.
- Bag, equipment wells, stats.
- Tooltip per 02: placement rule from 06 finding 6, `tip.panel`, `tip.band`.
- Context menu via `CharacterPanelModel.BackpackMenu` and `EquipmentMenu`; destroy confirm.
- Drag:
  - bag to bag: `Move`
  - bag to allowed well: `EquipTo`
  - well to bag: `Unequip`
  - invalid target: `cell.overlay.invalid`
  - drop outside: cancel
- Left click does nothing (menu and drag only). This differs from ImGui on purpose, to avoid mis-equips.
- Notices: loot lines with tier colour, rejection, world transfer, using flat bands (`#000` about 50%) and time-stepped rows.
- Death overlay: "You died" and respawn label.
- Actor name and level labels: text only, no HP-bar art yet.
- Combat numbers.

**Inert, empty art:** `gem.mp.empty` (no number), both `gauge.empty`, the three stone slabs with no icon, `qwe.slot.*.normal` with no letters and no key, banner and lens frames with no icon.

**Hidden:** chat cluster, currency row, repair and store, set-option tab, arrow, combine, extra wells, EXP "section" digits, Miss, Excellent, Ignore, Double, auras, Enhanced tier.

**Parity check:** every inventory action available in ImGui exists in classic, except "Move To" which is replaced by drag.

**Not changing:** gameplay rules, schema v9, persistence, content, bag size, input bindings other than F3.

---

## 5. Setup and workstreams

### Base setup (coordinator or C0 implementer)
```
git worktree add -b integrate/classic-hud .claude/worktrees/classic-hud-int master
# inside that worktree
git merge --no-ff integrate/ui-assets
cp G:/workspace/projects/dream/docs/superpowers/specs/2026-11-01-classic-mmo-ui/*.md docs/superpowers/specs/2026-11-01-classic-mmo-ui/
git add docs/superpowers/specs/2026-11-01-classic-mmo-ui/*.md && git commit
git -C G:/workspace/projects/dream diff -- docs/ledger/plans/2026-10-31-characters-items-enemies-roadmap.md | git apply && git add -A docs/ledger/plans && git commit
```
- Save this plan as `docs/ledger/plans/2026-11-01-classic-hud-plan.md` in the worktree.
- Do not stage `reference/`. Master stays dirty as it is. Leave `scratchpad_dummy` alone.

### C0 contracts (one commit before parallel work)
**Owns:**
- `dream/dream.csproj`: add `ui\**\*` to the existing `None Update` glob.
- `dream/Assets/AssetPaths.cs`: `UiDirectory`, `Ui()`.
- `dream/Rendering/Ui/`: `UiTypes.cs`, `IUiCanvas.cs`, `IUiInput.cs`, `IHudActions.cs`, `UiScale.cs`, `UiLayout.cs`, `HudView.cs`, `ClassicHud.cs`, `UiSpriteTable.cs` (with JSON parser and validation), `UiSpriteNames.cs` (required names).
- Stubs of all panels (`Panels/*.cs`) as empty `Build` methods.
- Stub `dream/Rendering/Core/Render.ClassicHud.cs`.
- `dream/Rendering/Hud/HudInteractionRequest.cs`: add trailing `bool CloseInventory = false`.

**Tests:** `dream.Tests/Rendering/Ui/UiScaleTests.cs`, `UiSpriteTableTests.cs` (parses the real JSON; every `items.json` key has a sprite; no banned names), `AssetPathsTests`.

**Done when:** it compiles, all existing tests pass, and the layout math in `UiLayout` is tested at 720p, 1080p, 1440x1024.

Implementer branches: `classic-hud/ws1-foundation`, `ws2-surface`, `ws3-status-bar`, `ws4-windows`, `ws5-combat-text`.

### WS1 foundation (GL layer)
**Owns:** `dream/Rendering/Ui/UiBatch.cs`, `UiAtlasStore.cs`, `UiFontSetup.cs`, `UiGlyphTable.cs`, `dream/Rendering/Ui/Gl/*`, `dream/shaders/uiVertex.glsl`, `uiFragment.glsl`, `dream/Rendering/Core/TextureDecoder.cs`, `Render.ImGui.cs`, `Render.Lifetime.cs`, `Render.ClassicHud.cs`, `dream.Tests/Support/UiSoftwareCompositor.cs`, `dream.Tests/Rendering/Ui/{UiBatch,UiAtlasStore,UiGlyphTable,HudPreview}Tests.cs`.

**Steps**
1. `DecodeRgba` (no flip, no premultiply) and upload variant with clamp, filter and max level. Existing methods unchanged.
2. `UiBatch` CPU side.
3. Atlas store with the state machine behind `IUiAtlasLoader`.
4. Glyph table built once from the ImGui font. Keep font index 0 as today; add the 26 px bake.
5. GL backend: preallocated buffers, GL state capture and restore (reuse `ImGuiGlStateSnapshot`), `RenderCounters.CountDraw`, dispose steps.
6. CPU compositor: bilinear sampling of real PNGs with ImageSharp, applying the same draw commands. Output to `artifacts/hud-preview/` under `DREAM_HUD_PREVIEW=1`.

**Tests:** quad and UV correctness with no V flip; 9-slice and tile geometry; clip behaviour; command merging; atlas failure matrix via fake loader; zero allocation for a full batch; structural test that dispose includes the UI step.

**Accept:** a compositor PNG of fixture sprites looks right. Atlas failure never throws out of the store.

### WS2 surface, admin key, input, view model
**Owns:** `dream/Application/GameSettings.cs`, `dream/Configuration/DesktopClientConfig.cs`, `DesktopClientTextConfig.cs`, new `dream/Configuration/HudOptions.cs`, `dream/Client/Scenes/PlayScene.cs`, `PlayScene.Update.cs`, `PlayScene.Render.cs`, `PlayScene.HudRequests.cs`, `dream/Rendering/Core/Render.Hud.cs`, `Render.HudPanels.cs`, `dream/Rendering/Hud/HudRenderer.cs`, new `dream/Rendering/Ui/HudSurfaceSelector.cs`, `UiInputRouter.cs`, `HudViewBuilder.cs`, `HudItemCatalog.cs`, `docs/configuration.md`, tests in `dream.Tests/Rendering/Ui/`, `dream.Tests/Application/`, `dream.Tests/Configuration/`.

**Steps**
1. `admin_hud`, `ui_scale`, env resolution, Settings UI scale slider, dev-section gating.
2. Selector and F3.
3. `DrawHUD` branch per section 3, with one try/catch at the surface boundary; `_render.HudScene` plain-struct setter, following `WorldTransferHud`.
4. Router and cursor state.
5. View builder: `item.<ItemKind>` lookup with `OrdinalIgnoreCase`, tier from rarity, unknown kind gives `HasIcon=false`. Hostile snapshots must not throw (same rule as `CharacterPanelModel`).
6. Project labels and combat text to screen in `Render`, pass as lists. Reuse `TryProjectWorldToScreen`.

**Tests:** config round trip with default-0 keys; env never persisted; selector transitions including latched failure; F3 no-op when unavailable; router click, drag, modal, not interactive in gameplay; view builder with bad snapshots; `AdminHud` token guard; `GameplayInputResumeFilter` unchanged via close-with-button-held case; zero allocation on an unchanged frame.

**Accept:** with classic stubs, the game runs with an empty HUD; `DREAM_HUD=imgui` equals today's behaviour; existing HUD tests pass unchanged apart from config text.

### WS3 status bar and notices
**Owns:** `dream/Rendering/Ui/Panels/{StatusBarPanel,NoticePanel,WorldLabelsPanel,CursorPanel}.cs`, `dream/Rendering/Hud/LootFeedbackSystem.cs` (add `GetRarity(int)`, additive), tests `dream.Tests/Rendering/Ui/Panels/{StatusBar,Notice,WorldLabels}PanelTests.cs`.

**Steps:** bar groups using `meta.layout` from `hud.json`, gap fill, talons over gem, numbers with dark halo, inert list from section 4, notices, death overlay, labels, gauntlet cursor at its pivot.

**Tests (fake `IUiCanvas` recording calls):** HP 0, 50, 100% clip rects; EXP with `ExperienceToNextLevel=0`; no MP number or text ever drawn; no icon drawn in inert slabs; group anchors at 16:9 and 21:9; notice cap and rarity colours; `%` in a name stays literal; allocation zero.

**Accept:** compositor preview of the bar at 720p, 1080p, 1440x1024 matches `tools/ui-assets/previews/hud_bar_1920.png` in structure.

### WS4 inventory and character windows
**Owns:** `dream/Rendering/Ui/Panels/{InventoryWindow,CharacterWindow,ItemOverlays,ItemTierStyle}.cs`, `dream/Rendering/Hud/CharacterPanelModel.cs` (stale comment and additive helpers only), tests `dream.Tests/Rendering/Ui/Panels/{InventoryWindow,CharacterWindow,ItemOverlays}Tests.cs`.

**Steps:** window chrome from 01 section 5 rects; 4x4 slots of 2x2 cells; fit-scale icons; tier page selection; wells; drag; tooltip; right-click menu; confirm; stat rows; Close button.

**Tests:**
- Fit scale for each footprint.
- Each drag outcome emits exactly one correct `InventoryHudAction`.
- Invalid well emits nothing; equipped item offers no Destroy.
- Destroy only after confirm, and the action carries `ItemInstanceId`.
- Snapshot change mid-drag cancels.
- Tooltip above and flip below, plus screen clamp.
- `RarityColorKey` parity.

**Accept:** every action reachable; empty inventory ("waiting") state; preview shows all 9 items in all 3 tiers.

### WS5 combat text and wire
**Owns:** `dream/Rendering/Ui/Panels/CombatTextPanel.cs`, `dream/Rendering/Hud/FloatingCombatText{Motion,RenderState,Style,Styles,System}.cs`, `CombatFeedbackSystem.cs`, `dream/Client/Scenes/PlayScene.Lifetime.cs`, `dream/Simulation/World/DamageEventDelta.cs`, `dream/Simulation/Runtime/WorldSessionRuntime.Replication.cs`, `dream/Multiplayer/Protocol/ServerSnapshotJsonConverters.cs`, `TransportEnvelope.cs`, `dream/Client/Replication/ClientSnapshotDamageApplier.cs`, `ClientAuthoritativeActionApplier.cs`, tests in `dream.Tests/Rendering/Hud/`, `dream.Tests/Client/Replication/`, `dream.Tests/Multiplayer/Protocol/`, new `dream.Tests/Integration/CombatTextLoopbackTests.cs`.

**Steps**
1. Write the loopback test first: one local hit, count `DamageAppliedEvent`. If it shows 2, remove the publish from the action applier and update `ClientAuthoritativeStateApplierTests`.
2. Add `FloatingCombatTextKind HitKind = Regular` as the last field of `DamageEventDelta`. JSON key `"hit"`, written only when not Regular. The client treats undefined values as Regular.
3. Pass `evt.Kind` at `Replication.cs:322`. Do **not** touch `CombatResolutionResult`.
4. Bump to 12 with the comment line. Update the snapshot applier to pass `HitKind`.
5. `CombatFeedbackSystem` optional `Func<int>? localActorId`: target is local player gives Incoming.
6. Render state gains `Kind`, `AgeSeconds`, spawn order. Classic curve from decision 19, cap 12, newest on top. Existing ImGui fields remain.
7. Panel draws `dmg.0-9` from `dmg_digits_x4` via `MaskGlyph`, advance 44/64 of height, centred. Colours per 05: Regular `#FDB442`/`#4A1800`, Critical `#0A9AFE`/`#001750`, Incoming `#FF0000`/`#400000`.
8. Integer to digits in place, no string parsing allocation on the panel.

**Tests:** omit-default bytes identical for a non-crit snapshot apart from the version digit; crit round trip; hostile `hit`; coalesce preserves it; `GoldenCheckpoints` and `PreExtractionGolden` unchanged; worst-case frame limit test still passes; panel zero allocation.

**Accept:** crit shows blue, incoming red, one number per hit. If WS5 slips, drop its wire commits and everything else ships.

### Merge order
C0 → WS1 → WS2 → WS3 → WS4 → WS5.

WS1 gives a real canvas, WS2 connects it. WS3 and WS4 touch only panels and could swap. WS5 is last because it is the only wire change and is droppable.

Then tester and reviewer per `CLAUDE.md`, and update `docs/rendering-and-assets.md`, plus a work log at `docs/ledger/work-log/2026-11-01-classic-hud.md` during integration.

---

## 6. Gates

Numbers are targets, because no baseline exists. **Measure the baseline on the base commit first**, using the same route below, with `DREAM_HUD=imgui` on a built branch and on master.

| Gate | Budget |
|---|---|
| FPS, 1080p, same scene, `DREAM_MAX_FPS=0` | at least 95% of baseline |
| HUD CPU (`HudDrawMilliseconds`) | at most baseline + 0.5 ms, under 1.5 ms absolute |
| GPU HUD section | under 1.0 ms at 1080p |
| Draw calls, HUD | at most 12 in play, at most 16 with windows and tooltip |
| Managed allocation per frame | 0 B in build and submit after warm-up, with a rebuild only on data change (test with `GC.GetAllocatedBytesForCurrentThread`, as in `HudTextAllocationTests`) |
| Atlas load | decode off-thread under 800 ms total; upload at most 1 atlas per frame, longest frame under 50 ms |
| GPU memory | about 50 MB base (window 17.8, hud 9.8, items 3 × 6.8, small others), about 65 MB with mips |
| Build output | +7.65 MB (7.61 MB PNG, 38 KB JSON), limit 8.5 MB |
| Tick goldens | `GoldenCheckpoints`, `PreExtractionGolden`, `MapSourceGoldenTests` unchanged |
| Byte pins | only the version digit moves, and only in WS5 |
| Client-only commits | C0 to WS4 change no Simulation, Server or Multiplayer file (verify by `git diff --stat`) |

### On-screen verification
1. **Headless compositor** (WS1): no GL, no window, no firewall. `DREAM_HUD_PREVIEW=1 dotnet test --filter HudPreview` writes PNGs for play, inventory open, tooltip, dead, each tier, at 720p, 1080p, 1440x1024. An agent can read these directly. It proves layout, UVs and z-order, not GL state.
2. **Real GL capture on loopback:** run `dream.exe` with `DREAM_AUTO_CONNECT=1` and **no** `DREAM_SERVER_HOST`. That uses the integrated loopback server, so no firewall prompt. Add env `DREAM_HUD_CAPTURE=<dir>` and `DREAM_HUD_CAPTURE_SCRIPT=play,inventory,admin` (WS2, small `dream/Client/Scenes/PlayScene.Capture.cs`): at fixed frames set state, `GL.ReadPixels` the back buffer before swap, save PNG with ImageSharp, then close. Add `DREAM_RENDER_PERF_LOG=1` for the perf gates. Avoid the harness's `--server` route for this.
3. **Fallback proof:** temporarily rename `ui/hud/hud.png` in a build output and confirm the ImGui HUD with one fault-log line.
4. **Human checklist in the handoff:** F3 toggle, V windows, drag, crit colour, death.

---

## 7. Risks and unknowns

**Risks**
- `ImFontPtr.FindGlyph` and multi-size bake are unproven in this ImGui.NET version. Fallback: read the font data once in `UiFontSetup`; if that fails too, it counts as a classic failure and latches ImGui.
- Mouse may mismatch on HiDPI, because `io.DisplaySize` uses `_currentSize` and framebuffer scale is unset. WS2 uses a single coordinate source.
- ProggyClean looks plain and small at 13 px against ornate art. Accepted until a licensed font exists.
- `CursorState.Hidden` lets the cursor leave the window during a drag. Drop outside cancels.
- Hiding dev sliders will surprise the user in their own play until `admin_hud=1`. Flag it at handoff.
- 64 MB texture memory on weak GPUs. `items_uncommon` and `items_rare` could be lazy later.
- Protocol v12 breaks old clients, as every bump does. Client and server ship together.
- Action-path damage removal could lose a number if a snapshot path ever misses that event. The loopback test covers it.
- Mip chain was only simulated by `check_loader.py` at 0.5625, not 0.375. Check the 720p preview for bleed.

**Could not establish**
- Actual FPS, draw counts, decode times: all budgets are estimates.
- Double numbers on screen: inferred only.
- Full `AppendEnvelope` content: I searched it rather than reading every line. WS5's run decides.
- Arial's licence, and whether the source-game screenshots may be kept in the repo long term.
- `.gitignore` handling of generated `artifacts/hud-preview` is covered (`artifacts/` is ignored), but whether `dream/config/desktop-client.txt` is tracked was not checked. WS2 must ensure no local config change is committed.
- The specs had no second pixel review after fix loop 1 (per 06), so INFERRED values remain unverified.
- Whether `ReadPixels` capture works before swap on the user's driver. The compositor route remains available if not.

**Open questions:** none. All points above have defaults.
