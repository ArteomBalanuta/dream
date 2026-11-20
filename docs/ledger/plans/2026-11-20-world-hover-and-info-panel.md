# Plan: unified world hover and the hover info panel

- Date: 2026-11-20
- Status: wave 1 built and integrated on `integrate/world-hover` (see `docs/ledger/work-log/2026-11-20-world-hover.md`);
  wave 2 (model highlight) not started. As built, the Lead wired click-to-move through `ClickMoveHost.TryPick`
  (not `ClickMoveController`) and retired `EnemyHoverProbe`.
- Base: `master` at `4d404c81`
- Audit: `docs/ledger/analysis/2026-11-20-world-hover-audit.md`
- Delivery: `CLAUDE.md` team workflow with `parallel-agent-delivery` (contracts frozen first, 3 parallel
  workstreams, one Lead integration step, a small wave 2).

## 1. Goal

One client-side pipeline answers "what world thing is under the cursor" every frame, for every hoverable kind
(enemy, NPC, ground item, remote player, later interactables), as a stable **identity**, and drives three
consumers from that one answer:

1. cursor feedback (sword over an enemy, speech over an NPC, hand over loot),
2. a model highlight on the hovered actor,
3. a compact info panel (name, level, HP/resource bars, key stats, action hint), built from a typed view model
   so the HUD never knows entity types.

Right click and the cursor aim consume the same answer, so what the cursor shows is by construction what a click
hits.

Non-goals (first pass): mana for enemies/remote players (needs protocol 23 - section 9), a ground selection-ring
decal, gamepad/soft-target hover, hover on the ImGui fallback HUD, tooltips for HUD widgets (they have their own).

## 2. Architecture

```
                 Update, once per frame                                    Render
WorldPointerState ──► WorldHoverPicker ──► HoverTracker ──► HoverFrame ──┬► Render.Hover ─► ClassicHudFrame.Hover
 (OverHud, pos)        one ray, one wall    stickiness,      (Current id,  │                    └► HudView.Hover
                       march, N sources     dwell, linger,   transition,   │                         ├► CursorPanel (kind)
LabelProxy ───────────────────────────────► enter/stay/exit  panel alpha)  │                         └► HoverInfoPanel
 (label hot last frame)                                                    ├► CursorAimController (enemy anchor)
                                                                           ├► ClickMoveController (right press target)
                                                                           ├► ResolveZoomAnchor (reuse, no re-pick)
                                                                           └► HoverInfoComposer ─► HoverInfo (view model)
                                                                                 IHoverInfoProvider per kind,
                                                                                 re-read live each frame
Renderers (EnemyRenderer, NpcModelRenderer) read Render.Hover.Current for the highlight tint.
```

Layering: `Dream.Client.Hover` (picking, tracking, providers) depends on simulation/replication caches;
`Dream.Rendering.Ui` depends only on the view-model structs (`HoverInfo`, `HoverPanelState`), never on
`EnemyActor`, `ClientNpc` or the caches. That is the "no entity logic in the HUD" boundary, and a test pins it
(no `Dream.Simulation.Enemies` / `Dream.Client.Replication` using in `Rendering/Ui/Panels/HoverInfoPanel.cs`).

## 3. Contracts (frozen before any parallel work)

All new files; namespace `Dream.Client.Hover` unless noted. Value types only on per-frame paths.

**As frozen (supersedes the sketch below where they differ):** the code is the contract -
`dream/Client/Hover/HoverContracts.cs` (`HoverPriority`, `HoverRay`, `HoverHit`, `IHoverSource`,
`IHoverInfoProvider`, `HoverPhase`, `HoverOrigin`, `HoverFrame`, `HoverUpdate`), `dream/Rendering/Ui/HoverInfo.cs`
(`HoverKind`, `HoverTargetId`, `HoverDisposition`, `HoverBarKind`, `HoverBar`, `HoverLine`, `HoverInfo`,
`HoverPanelState`), the `HudView` fields (`HoverCursorKind`, derived `PointerOverEnemy`, `HoverPanel`,
`HoverInfo?`), and the stub classes the Lead calls: `WorldHover` (the one facade: `Update(in HoverUpdate)`,
`Frame`, `Clear()`), the four `Sources/*HoverSource`, `HoverInfoComposer` (`Info`, `Compose(target)`), the four
`Providers/*HoverInfoProvider`, and `Panels/HoverInfoPanel.Build(c, i, v, l, s)`. `IHoverSource.TryHit` takes
`(in HoverRay, sticky, inflate, out HoverHit)`; the wall depth travels in the ray.

```csharp
public enum HoverKind : byte { None, Enemy, Npc, GroundItem, RemotePlayer, Interactable }

// Identity of a hoverable thing. Id is the kind's own stable key: enemy actor id
// (Combatant.Actor.Id), NPC ActorId, ground ItemInstanceId, remote player ActorId.
public readonly record struct HoverTargetId(HoverKind Kind, long Id)
{
    public static readonly HoverTargetId None = default;
    public bool IsNone => Kind == HoverKind.None;
}

// Lower value wins a depth tie (see 4.2).
public static class HoverPriority
{
    public const float DepthTieMetres = 0.35f;
    public static int Of(HoverKind kind); // Enemy 0, RemotePlayer 1, Npc 2, Interactable 3, GroundItem 4
}

public readonly record struct HoverHit(HoverTargetId Target, float Depth, Vector3 Anchor);

// One per kind. Allocation-free; called once per frame with the shared ray.
public interface IHoverSource
{
    HoverKind Kind { get; }
    // Best hit of this kind with Depth <= maxDepth, honouring `inflate` for the sticky target only.
    bool TryHit(in HoverRay ray, float maxDepth, HoverTargetId sticky, float inflate, out HoverHit hit);
    // Whether id still exists and is hoverable (alive, still in the cache); also its current anchor.
    bool TryGetLive(HoverTargetId id, out Vector3 anchor);
}

public readonly record struct HoverRay(Vector3 Origin, Vector3 Direction, float WallDepth);

public enum HoverPhase : byte { None, Entered, Stay, Exited }

// The one answer per frame. Current is what cursor, click and aim use (no dwell);
// Panel* is the debounced presentation of the info panel.
public readonly record struct HoverFrame(
    HoverTargetId Current,
    HoverTargetId Previous,
    HoverPhase Phase,           // transition of Current this frame
    Vector3 Anchor,             // Current's live anchor (combat anchor for actors)
    HoverOrigin Origin,         // Ray or Label
    HoverTargetId PanelTarget,  // may differ from Current while lingering
    float PanelAlpha,           // 0..1, 0 = hidden
    float DwellSeconds);

public enum HoverOrigin : byte { None, Ray, Label }

// Kind-agnostic view model the HUD draws. Fixed capacity, filled in place, no allocations.
public enum HoverDisposition : byte { Neutral, Hostile, Friendly, Party, Item }
public enum HoverBarKind : byte { Health, Mana, Stamina }
public readonly record struct HoverBar(HoverBarKind Kind, int Current, int Max, string Text);
public readonly record struct HoverLine(string Label, string Value);

public sealed class HoverInfo                     // namespace Dream.Rendering.Ui (shared view model)
{
    public const int MaxBars = 2, MaxLines = 4;
    public HoverTargetId Target;                  // copied, for widget/animation identity
    public HoverDisposition Disposition;
    public string Title = "";                     // display name (server strings: clipped by the panel)
    public string Subtitle = "";                  // "Lv 7 Skeleton", "Vendor", "Rare sword"
    public ItemTier Tier;                         // title colour for ground items
    public int BarCount; public readonly HoverBar[] Bars = new HoverBar[MaxBars];
    public int LineCount; public readonly HoverLine[] Lines = new HoverLine[MaxLines];
    public string Hint = "";                      // "[RMB] Attack", "[E] Talk", "[E] Pick up", "Claimed"
    public bool Defeated;                         // HP reached 0 while shown (linger frame)
    public void Clear();
}

public interface IHoverInfoProvider
{
    HoverKind Kind { get; }
    // Fills info for id from live client state; false when id is gone. Must not allocate when values are unchanged.
    bool TryDescribe(HoverTargetId id, HoverInfo info);
}

// HUD side (namespace Dream.Rendering.Ui)
public readonly record struct HoverPanelState(bool Visible, float Alpha, HoverKind Kind);
// HudView gains:   HoverPanelState HoverPanel; HoverInfo HoverInfo (reused instance); HoverKind HoverCursorKind;
// HudView keeps PointerOverEnemy as  => HoverCursorKind == HoverKind.Enemy  (compat for existing tests).
// UiCursorKind gains: Talk, Take, Use.   UiCursor.Select(dragging, leftDown, HoverKind hovered).
```

The Lead commits these as compiling stubs (`TryHit` returns false, `TryDescribe` returns false,
`HoverInfoPanel.Build` draws nothing) in one `chore(hover): freeze contracts` commit; that sha is the base for all
workstreams.

## 4. Behaviour

### 4.1 Ordering and precedence (per frame, in `PlayScene.Update`)

1. **Gates clear hover** (`Current = None`, panel hides with no linger): loading, `!allowGameplayInput` (chat
   typing, dead, settings), window unfocused (capture counts as focused, as today), a modal open, a camera
   rotation drag held, an inventory drag in progress.
2. **UI first.** If `pointer.OverHud` and the router's hot widget is **not** a world label, `Current = None`.
3. **Label proxy.** If last frame's hot widget is a world label (`WorldLabelsPanel` ids, which already carry
   `HudLabelClick(kind, id)`), `Current = that label's target`, `Origin = Label`. A label stands for its entity, so
   hovering an NPC's name gives the Talk cursor and the NPC panel. The label proxy beats the ray.
4. **Ray, throttled.** A full pick (build the ray, march the wall once - `HoverRay.WallDepth`, the same 5 cm
   march and result as today - and ask every `IHoverSource` for its best hit in front of the wall) runs only when
   it is due: the pointer moved more than `HoverTuning.RepickPixels` (2 px), the view or projection matrix changed,
   `ForcePick` (a button went down this frame), or `HoverTuning.RefreshSeconds` (0.10 s) passed since the last
   pick (things move under a still cursor). Otherwise the last answer is reused and only re-validated with
   `IHoverSource.TryGetLive` (an id lookup), which also refreshes its anchor. A still mouse over a still camera
   costs about 10 picks a second, not one per frame.
5. Resolve (4.2), apply stickiness (4.3), advance the tracker (4.4), publish `HoverFrame`.

The info panel is **non-interactive**: it registers no widget id and never counts toward `OverHud`. Otherwise the
panel appearing under the pointer would flip `OverHud`, end the hover and hide the panel - an oscillation.

### 4.2 Deterministic resolution

Among the per-kind hits (each already in front of the wall):

1. smallest `Depth` wins, **unless** two hits are within `HoverPriority.DepthTieMetres` of each other: then the
   lower `HoverPriority.Of(kind)` wins (an enemy standing on a loot drop beats the loot);
2. same kind and still tied: lower `Id` wins (stable, never list order).

Hit volumes per kind (replace the fixed 1.0 m sphere):

| Kind | Volume | Anchor reported |
| --- | --- | --- |
| Enemy | vertical capsule from feet to `CollisionHeight`, radius `max(CollisionRadius, 0.45)`, at the **render pose** (what the player sees) | combat anchor (unchanged, aim and click use it) |
| Remote player | capsule 1.8 m x 0.4 m at the interpolated pose | pose + 1.0 m |
| NPC | capsule 1.9 m x 0.45 m | label anchor |
| Ground item | sphere 0.45 m at the item (small things need a generous target) | item position |

Enemy agreement: right click and the cursor aim **consume `HoverFrame.Current`** instead of re-picking, so they
cannot disagree. `EnemyHoverClickAgreementTests` is rewritten to assert that the attack a right press issues
targets `Current`, plus the existing wall cases.

### 4.3 Stickiness (anti-flicker)

The current target is passed to its source as `sticky` with `inflate = 1.25` (25 % larger volume). It stays hovered
while the ray still hits the inflated volume in front of the wall, unless a different hit is in front of it by more
than `DepthTieMetres`. A new target needs a hit on the normal volume.

### 4.4 Lifecycle, debounce and linger (`HoverTracker`, pure, takes `dt`)

| Event | Cursor / click / aim / highlight | Info panel |
| --- | --- | --- |
| None -> A | immediate (`Phase = Entered`) | shows after **0.10 s** dwell on A (sweeping across a pack shows nothing); fade-in 0.08 s |
| A held | `Stay` | visible, values re-read every frame |
| A -> B while the panel is visible | immediate | swaps to B at once ("warm" - no new dwell), no fade |
| A -> None (pointer left) | immediate (`Exited`) | lingers **0.15 s** on A's last live values, then fades 0.10 s; re-entering A inside the linger cancels it |
| A despawns / dies while hovered | immediate `Exited` | shows A with `Defeated = true` and HP 0 for 0.25 s, then fades; no live re-read (A is gone) |
| A gate closes (4.1 step 1) | immediate | hidden at once, no linger |

Events are a **pull model**: consumers read `HoverFrame.Phase` once per frame. No C# events or delegates on the
per-frame path (allocation and re-entrancy free). A consumer that needs edges (an audio `ui_hover.wav` tick on
Entered of an Npc/GroundItem, future quest triggers) reads `Phase`.

Constants live in one `HoverTuning` static class; tests pin the table above.

### 4.5 Reactive binding

`HoverInfoComposer` owns one `HoverInfo` and the providers. Each Render it calls the provider for `PanelTarget`
only (one entity, so cost is constant). Providers read live state - `EnemyActor.Combatant.Health`,
`RemotePlayerPresentationState`, `ClientNpcCache`, `ClientGroundItemCache` - so HP updates as snapshots land.
Strings come from value-keyed caches (the `ActorHealthLabelCache` pattern): `"Lv 7 Skeleton"` keyed by
(level, kind), `"80 / 100"` keyed by (cur, max). Zero bytes per frame in steady state; a value change allocates one
string, bounded by the cache cap.

Per-kind content (first pass):

| Kind | Title | Subtitle | Bars | Lines (<= 4) | Hint |
| --- | --- | --- | --- | --- | --- |
| Enemy | `DisplayName` | `Lv N <EnemyKind>` | Health | Damage `AttackDamage`, Speed `AttackIntervalSeconds` as "fast/normal/slow", State (Idle/Hunting/Attacking) | `[RMB] Attack` |
| Remote player | `DisplayName` | `Lv N` (+ "Party" when in the party) | Health | - | - |
| NPC | `DisplayName` | role when known, else empty | - | - | `[E] Talk` in range, else distance |
| Ground item | catalog `DisplayName` (tier colour) | rarity | - | first catalog line | `[E] Pick up` / `Claimed` |

Disposition drives the title colour and the frame accent: Hostile = dim blood red, Friendly = bone, Party = dim
mint, Item = tier colour. All within the albedo rule (UI colours are exempt from it, but follow
`hud-art-direction`: dark, grim, gothic).

### 4.6 Placement and clamping (`HoverPanelPlacement`, pure)

- Mode **Cursor** (default): top-left at pointer + (18, 22) UI units x scale, i.e. below-right, clear of the sword
  sprite's rect (`UiCursor.Rect`).
- Flip to the left of the pointer when the right edge would cross the viewport, above when the bottom would.
- Clamp into the viewport with a 6-unit margin, then push out of `UiLayout.Bar` (and the open inventory and
  character windows) vertically; never overlap the cursor rect.
- Round to whole pixels; width is fixed per kind (enemy 140 units) so the panel does not breathe as HP text changes.
- Mode **Anchored** (setting, later): above the target's label anchor projection, same clamps. The plan builds the
  placement function for both modes; only Cursor is wired in the first pass.
- While the panel shows an enemy, that enemy's floating label hides (the panel says the same thing better);
  other labels are unaffected.

### 4.7 Visual feedback

- Cursor (`UiCursor.Select(dragging, leftDown, hovered)`): Grab while dragging > Attack (Enemy) > Talk (Npc) >
  Take (GroundItem) > Use (Interactable) > Pressed > Pointer. Remote players keep the pointer. New sprites
  `cursor.talk`, `cursor.take`, `cursor.use` come from `gen_hud.py` with pivots from `tip_pivot` and `check_hud.py`
  gates with synthetic negatives, as the sword did.
- Highlight (wave 2): `EnemyRenderer` adds `HoverTuning.HighlightTint` to the hovered enemy's `TintOffset`;
  `NpcModelRenderer` the same for the hovered NPC (check its tint path first - if it has none, it gets the
  enemy program's `ActorTintOffset` uniform). Keep the sum under the 0.40-per-channel albedo rule. Verify in a
  capture against the wolf and the skeleton at day and night (`tod:`).

## 5. Performance rules

Performance matters but is not the focus: keep the obvious costs out, do not micro-optimise.

- Picks are throttled (4.1 step 4): at most one ray and one wall march per pick, about 10 picks a second when
  nothing moves, shared by hover, click, aim and zoom anchor (`ResolveZoomAnchor` reads `HoverFrame` instead of
  re-picking for the enemy case).
- Each source: linear scan with a cheap reject (`dot(toTarget, dir) < 0 || > maxDepth`) before the capsule test.
  No spatial index: tens of enemies, <= 256 ground items, few NPCs.
- `AllocationAssert.AtMost(0)` over: picker + tracker for 120 frames with targets entering and leaving; composer
  for a hovered enemy whose HP is unchanged; `HoverInfoPanel.Build` with a fake canvas.
- The panel draws after `ChatPanel` and before `ItemOverlays` (item tooltips and the drag ghost stay on top), and
  respects `IUiCanvas.QuadHeadroom` like `WorldLabelsPanel`.

## 6. Ownership map and workstreams

Wave 0 - **Lead** (contracts): `Client/Hover/HoverContracts.cs`, `Rendering/Ui/HoverInfo.cs`, the stub classes
listed in section 3, `HudView.cs` fields. Commit = base sha. `UiCursorKind` values are WS3's (its file).

Wave 1 - three implementers in parallel, disjoint files:

| WS | Owns (new unless marked) | Builds | Skills / tools | Acceptance |
| --- | --- | --- | --- | --- |
| **WS1 Hover core** | `Client/Hover/WorldHoverPicker.cs`, `HoverTracker.cs`, `HoverTuning.cs`, `Sources/EnemyHoverSource.cs`, `NpcHoverSource.cs`, `GroundItemHoverSource.cs`, `RemotePlayerHoverSource.cs`, `LabelHoverProxy.cs`; edits `Client/Input/WorldCursorPicker.cs` (extract the wall march as `WallDepth`), `Client/Input/EnemyHoverProbe.cs` (becomes a thin adapter or is deleted), `Client/Input/ClickMoveController.cs` (right press takes a `HoverFrame`); tests under `dream.Tests/Client/Hover/` and the rewritten `EnemyHoverClickAgreementTests`, `EnemyHoverProbeTests`, `WorldCursorPickerHoverTests` | 4.1-4.4, sections 5 rules for the picker | `tools/dev/test.ps1 -Area Client` | resolution table and tie rule tests; stickiness flicker test (pose jitter at the edge: 0 transitions over 60 frames); lifecycle table tests with synthetic `dt`; click == hover in all wall cases; 0 alloc |
| **WS2 Descriptors** | `Client/Hover/HoverInfoComposer.cs`, `Providers/EnemyHoverInfoProvider.cs`, `NpcHoverInfoProvider.cs`, `GroundItemHoverInfoProvider.cs`, `RemotePlayerHoverInfoProvider.cs`, `HoverTextCache.cs`; tests under `dream.Tests/Client/Hover/Providers/` | 4.5 | `-Area Client`, `-Area Content` if catalog lookups change | per-kind content table; live HP change reflected next frame; `Defeated` path; hostile wire strings (huge level, negative HP, 4 KB name) clamped; 0 alloc with unchanged values |
| **WS3 HUD presentation** | `Rendering/Ui/Panels/HoverInfoPanel.cs`, `Rendering/Ui/HoverPanelPlacement.cs`, edits `Rendering/Ui/UiCursor.cs`, `Rendering/Ui/Panels/CursorPanel.cs`, `Rendering/Ui/UiSpriteNames.cs`, `tools/ui-assets/gen_hud.py` (cursor art only), `tools/ui-assets/tests/check_hud.py`, regenerated `ui/hud/cursors.*`; tests under `dream.Tests/Rendering/Ui/` | 4.6, 4.7 cursor part, panel drawing from `HoverInfo` only | `generating-dream-assets`, `capturing-dream-screenshots` (headless via `HudPreviewTests` + `UiSoftwareCompositor`, `DREAM_HUD_PREVIEW=1`; live wiring is the Lead's), `-Area Rendering` | placement tests (4 corners, flip, bar push-out, never under the cursor rect, scales 1-4); panel draws nothing at alpha 0; text clipped to width; no widget ids registered; boundary test (no simulation/replication namespaces); art gates with negatives; a `HudPreviewTests` PNG of the enemy, NPC and item panels |

Wave 1 integration - **Lead** (hot paths, nobody else touches them):

- `Client/Scenes/PlayScene.Update.cs`: replace the `EnemyHoverProbe` block with `WorldHoverPicker` + `HoverTracker`;
  feed `HoverFrame` to the cursor aim, click-to-move and `Render.Hover`.
- `Client/Scenes/PlayScene.cs` (`ClickMoveHost`), `PlayScene.Camera.cs` (`ResolveZoomAnchor` reads the frame).
- `Rendering/Core/Render.Hud.cs` (`Render.Hover`, `ClassicHudFrame` fields, composer call, `PointerOverEnemy`
  becomes derived), `Rendering/Ui/ClassicHudSurface.cs` (frame -> view), `Rendering/Ui/ClassicHud.cs` (panel in
  the build order), `Rendering/Ui/Panels/WorldLabelsPanel.cs` (expose the hot label's target for the proxy; hide
  the hovered enemy's label).
- Then: `-Area Client`, `-Area Rendering`, full suite once, captures (enemy hover at day and night, NPC label
  hover, ground item, panel at all four screen corners, ui_scale 1 and 4), `tools/golden/run.ps1 -Scene all`
  (expect only cursor-area changes; re-record only those, then `tools/golden/tests/test_golden.py`).

Wave 2 - one implementer after wave 1 is merged: **highlight** (`Rendering/Actors/EnemyRenderer.cs`,
`NpcModelRenderer.cs`, its tests) per 4.7, with captures.

Merge order: contracts -> WS1 -> WS2 -> WS3 -> Lead wiring -> wave 2. WS1/WS2/WS3 share no file, so any order
merges cleanly; this order lets each merge build.

## 7. Briefing reminders for every implementer

No `git stash` (WIP commits). Reset the worktree onto the integration branch (`integrate/world-hover`, created from
`master` with the contracts commit) before starting. Build with `/p:BaseOutputPath=artifacts/verify-bin/`; never
kill a running `dream.exe`. One test suite at a time, through `tools/dev/test.ps1` (tee'd, watchdog). After a
content or asset change, captures need `-FromSource` and `DREAM_AUTO_CONNECT=1`. Look at visual changes in a
capture before calling them done. Every new gate gets a synthetic negative.

## 8. Risks

- **Click behaviour change.** Right click now follows the sticky/inflated hover, not a fresh 1.0 m sphere. It is a
  deliberate improvement (bigger, steadier target) but changes which enemy a click picks in edge cases; the
  agreement tests are rewritten, not deleted, and the reviewer must check them.
- **Press/release identity** (memory: found 7x): any click acting on a hover target must key on `HoverTargetId`,
  never a list index, and re-check the target is live at the moment of effect.
- **Label proxy lag.** The router's hot id is one frame old, as `OverHud` already is; acceptable, but the proxy
  must drop a target whose label vanished this frame.
- **Golden scenes**: cursor changes fail every golden scene (swarm-lead-traps); budget a re-record.

## 9. Follow-ups (out of scope here)

- Mana/resource bars for enemies and remote players: add the field to the enemy and player snapshot rows, bump the
  protocol to 23 (checklist in the skill-effect-visuals plan), then `HoverBar(Mana, ...)` lights up with no HUD
  change - the point of the typed view model.
- NPC role/level on the wire (vendor, quest giver) for the NPC subtitle.
- Interactables (workstations, doors, chests): a new `IHoverSource` + `IHoverInfoProvider` pair, a `cursor.use`
  hookup, nothing in the HUD.
- Ground selection ring decal under the hovered/targeted actor.
