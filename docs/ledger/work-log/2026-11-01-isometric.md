# Isometric view and click-to-move

Integration of the plan in `docs/ledger/plans/2026-11-01-isometric-plan.md`. Branch `integrate/isometric`, base ec20684.

## Merges
nav 23134bf, iso-view d724a28, click-input 1b08242. All clean, filtered tests green after each.

## Glue
- `CellOf` matched `NavCell.FromBody`, so it was removed in favour of `FromBody`.
- One replan counter, in `PathFollower`. Three stuck replans run, the next stuck verdict sets `GaveUp` and returns `Arrived`. A short walk back no longer clears the count: only 2 m from the last replan, a normal `SetPath` or `Clear` do.
- `LastWorldCamera` and `ProjectionMatrix` are the pair of the last Render: the camera is the one `DrawWorld` got, and the projection only changes on resize.

## Stuck clicks
Reproduced with `ClickSoakTests`: the real generated terrain, controller, pathfinder, follower and Bullet body, 9 target kinds, each click starting where the last ended. Causes found:
- Pushing on a face while jumping: the motor resets speed every tick, so wall friction eats the jump. The body rose 0.08 m, or hung in mid-air. The follower now stops pushing, hops, and walks on when the feet clear the step. In mid-air it stops pushing and lets the body fall.
- At rest on an exact cell boundary on raised cubes the body catches on the seam. A pinned body now jumps.
- A cube one cell above the walk level leaves a slot with no clearance. Paths now need headroom, including under the edge of a drop. Only the start cell may lack it.
- A body flush with a wall could not slide along it. Cells beside walls cost more, and waypoints stand 6 cm off a one-sided wall. The gap stays zero beside one-wide gaps.
- A tick step is wider than the arrival radius, so a body could orbit a waypoint for ever. The last and straight waypoints now use the wider radius.
- The sideways nudge overshot one-wide gaps for ever. Under one tick of error the body now backs off and lets the line correction settle.
- Clicks past the map edge did nothing. They now aim at the nearest ground cell.

| 2170 clicks | Arrived | Nearest spot | No pick | Gave up | Timeout |
|---|---|---|---|---|---|
| Before | 941 | 417 | 245 | 556 | 11 |
| After | 1532 | 633 | 3 | 2 | 0 |

## Real game
(25,17) is standable but sealed: pyramid, rubble cluster and a diagonal crack. The proving target moved to (27,20).

Goldens passed in the full run. The suite's one failure was `SkyPassContractTests` reading the old clear colour line.

## Still open
- Night at the default brightness is almost black at this camera distance.
- With the windows open the character sits under the Character window.
- Feel has not been judged by a person.
