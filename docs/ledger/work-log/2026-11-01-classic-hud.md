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
