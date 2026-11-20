# Analysis: cursor, picking, HUD and world-label architecture (for world hover + info panel)

- Date: 2026-11-20
- Base: `master` at `4d404c81`
- Purpose: find what a unified world-hover and info-panel system can reuse, and what blocks it.
- Companion plan: `docs/ledger/plans/2026-11-20-world-hover-and-info-panel.md`

## The pipeline today

```
Update (PlayScene.Update.cs:85)
  Render.SampleWorldPointer            -> WorldPointerState(Position, LeftDown, RightDown, OverHud)
     OverHud = Settings open | ImGui wants mouse | classic router HotId/Dragging/Modal (last frame)
             | ClassicHudHitTest.Over(bar, inventory+character windows)
  EnemyHoverProbe.Evaluate             -> bool + enemy anchor
     ClickMoveHost.TryGetEnemyAnchorAt -> WorldCursorPicker.TryBuildRay
                                       -> WorldCursorPicker.PickHoveredEnemy
                                            WallDistance: 5 cm march, up to 1200 IsBlocked samples
                                            EnemyRegistry.TryResolveHit: linear scan, sphere r=1.0 m at the combat anchor
  Render.PointerOverEnemy = bool       -> ClassicHudFrame -> HudView.PointerOverEnemy -> CursorPanel (Attack sword)
  CursorAimController.OnFrame(anchor)  (aim at the hovered enemy)
  ClickMoveController.OnFrame          (right press: its own full WorldCursorPicker.Pick, a second march)
  UpdateCameraInput -> ResolveZoomAnchor (on zoom: TryGetEnemyAnchorAt again, then TryPick: up to two more marches)

Render (Render.Hud.cs:350, Render.GroundItems.cs:111)
  ProjectClassicLabels: remote players + enemies (ActorWorldLabelBuilder, ActorHealthLabelCache)
                        + NPCs and nearest 8 ground items (WorldEntityRenderer.CollectLabels)
  ClassicHud.Build order: WorldLabelsPanel, CombatText, windows, StatusBar, Party, QuestLog, Notice, Chat,
                          ItemOverlays (tooltip, menu, drag ghost), CursorPanel
```

## What is already good (reuse, do not rebuild)

| Piece | Why it is worth keeping |
| --- | --- |
| `WorldCursorPicker.TryBuildRay` | Pure, tested inverse view-projection ray; handles degenerate viewports and non-finite pixels. |
| Wall-occlusion rule ("an entity wins only in front of the first blocked cell") | Already shared by hover and right click, pinned by `EnemyHoverClickAgreementTests` (8 cases). |
| `PickHoveredEnemy` allocation-free path | Pinned by `AllocationAssert.AtMost(0)`; the pattern the new picker must keep. |
| `WorldPointerState.OverHud` | The single "is the pointer on UI" answer already shared by click-to-move, camera wheel and hover. |
| `UiCursor.Select` / `CursorPanel` | One sprite cursor for HUD, menus and startup; adding kinds is a table change plus art. |
| `ItemOverlays.PlaceTooltip` + `TooltipContent` | Existing viewport-clamped placement and cached line building for item tooltips. |
| `ActorHealthLabelCache` | Allocation-free health strings keyed by value; the pattern for live bar text. |
| `HudWidgetId.Of(kind, id)` in `WorldLabelsPanel` | Click identity keyed on the target id, not the list index (BH-INPUT-1). |
| `EnemyRenderer` per-visual `TintOffset` | A ready hook for a hover highlight without a new render pass. |

## Gaps and coupling

1. **Hover is a bool, not an identity.** `PointerOverEnemy` carries no target id, so nothing can say *which* enemy is
   hovered, raise enter/exit, show a panel, or highlight a model. This is the root gap.
2. **Only enemies are pickable by ray.** NPCs, ground items and remote players are reachable only through their
   floating text labels (`WorldLabelsPanel` buttons). Hovering an NPC's body does nothing; hovering its label gives a
   fill but no cursor change or info.
3. **Enemy-specific plumbing end to end.** `IClickMoveHost.TryGetEnemyAnchorAt`, `EnemyHoverProbe`,
   `Render.PointerOverEnemy`, `ClassicHudFrame.PointerOverEnemy`, `HudView.PointerOverEnemy`, `UiCursor.Select(overEnemy)`.
   Each new entity type would add another bool through five layers.
4. **Hit volume ignores the actor's size.** `TryResolveHit` tests a fixed 1.0 m sphere around the combat anchor. A
   tall skeleton's head or a wolf's tail is a miss; two adjacent enemies resolve by ray depth of their anchors only,
   with no tie rule (equal depth -> whichever comes first in `_actors`, i.e. registry order).
5. **No stickiness.** A pointer resting on the edge of the 1.0 m sphere flips hover on and off as the enemy's
   interpolated pose moves; the cursor flickers between gauntlet and sword. Any panel built on this would flicker too.
6. **Labels and hover disagree.** A label is UI (it sets the router's hot id, so `OverHud` turns true next frame) yet
   it stands for a world entity. Hovering an enemy's label today *removes* the enemy hover. The labels are also
   built under every other panel and are disabled under the bar/windows, but not under chat, party or quest plates
   (`ClassicHudHitTest` has no rects for those - documented limitation).
7. **No enter/stay/exit lifecycle and no timing.** No debounce, linger or despawn handling exists.
8. **Live data reaches the HUD only as pre-formatted strings.** Enemy labels are `"Lv 3  80/100"`; there is no
   numeric HP/max in `HudView`, so a bar cannot be drawn without re-parsing or a new path.
9. **Data gaps on the wire.** Enemies replicate level, HP/max, kind, behaviour state; **no mana**. Remote players
   replicate level and HP; **no mana**. `ClientNpc` has name, visual key and position only (no role, level or HP).
   A mana bar for enemies/players needs a protocol change (current version 22) - out of scope for the first pass.

## Performance findings

| Site | Cost per frame | Verdict |
| --- | --- | --- |
| `WallDistance` 5 cm march | up to 1200 `IsBlocked` samples | Not measured; cheap in absolute terms, but it runs **2-4 times** per frame with a zoom (hover, click repath, zoom anchor x2). Compute once, share. |
| `EnemyRegistry.TryResolveHit` | O(enemies) | Enemy counts are tens, not thousands; no spatial index is warranted. Add a cheap ray-vs-distance prefilter, not a grid. |
| `WorldEntityRenderer.CollectLabels` | O(NPCs + ground<=256) | Already range-filtered; fine. |
| `ActorHealthLabelCache` | 0 alloc steady state | Fine. |
| Label projection | O(labels) `TryProjectWorldToScreen` | Fine. |
| Any new hover path | must stay **0 bytes/frame** | Gate with `AllocationAssert.AtMost(0)` as the enemy probe is gated today. |

Conclusion: there is no throughput bottleneck to fix; the risks are allocation regressions in a new per-frame path,
and redundant re-picks. The design shares one pick per frame and keeps every per-frame path allocation-free.
