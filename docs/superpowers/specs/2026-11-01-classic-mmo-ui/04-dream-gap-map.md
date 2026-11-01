# 04: dream HUD/UI audit and gap map for the classic MMO reskin

Code audit by reading only; nothing was built or run. Line numbers are as of the commits below.

## Branches and method

- **M** = `master` (d504d4d). **I** = `integrate/engine-iteration` (ee50e67), 95 commits ahead and 0 behind.
- I was read from worktree `.claude/worktrees/agent-a6500830ff69d83a6`, at the same HEAD.
- Relative paths are the same on both branches.
- **Identical blobs on M and I (line numbers hold on both):** `dream/Rendering/Core/Render.Hud.cs`, `Render.ImGui.cs`; `dream/Rendering/Hud/HudRenderer.cs`, `HudLayout.cs`, `HudText.cs`, `FloatingCombatTextSystem.cs`, `FloatingCombatTextStyles.cs`, `CombatFeedbackSystem.cs`; `dream/Client/Replication/ClientSnapshotDamageApplier.cs`, `ClientAuthoritativeActionApplier.cs`; `dream/Simulation/Combat/DamageAppliedEvent.cs`; `dream/Application/RuntimeState.cs`; `dream/Client/Input/PlayerController.cs`; `dream/Rendering/Core/TextureDecoder.cs`; `dream/Assets/AssetPaths.cs`; `dream/dream.csproj`; `Directory.Packages.props`.
- **HUD-related diffs M to I:** `Render.HudPanels.cs` (549 to 767 lines), `HudInteractionRequest.cs`, `HudStatusSnapshot.cs`. New on I: `CharacterPanelModel.cs`, `LootFeedbackSystem.cs`.
- `Render.HudPanels.cs` line numbers below are I unless marked M.
- Protocol version: M = 8 (`TransportEnvelope.cs:24`), I = 11 (`TransportEnvelope.cs:34`).

**Recommendation: base the reskin on I.** M has no rarity, no rolls, no four-slot equipment, no move/unequip/destroy, no loot feed and no death overlay.

## Summary

- The whole UI is Dear ImGui (ImGui.NET 1.91.6.1), with default ImGui look and font. The only custom GL draw is the 8-vertex red crosshair.
- No UI texture, icon, atlas, 9-slice, gauge, orb, drag and drop, hotbar, chat, mana, gold or skill exists on either branch.
- The ImGui renderer already binds any GL texture handle per draw command and honours scissor, so textured UI through `ImGui.Image`/`ImDrawList` should be possible with no new renderer. No such code path has been exercised in this repo.
- Biggest gameplay-side blockers:
  - Mouse: grabbed in play, so the HUD cannot be clicked without opening an overlay.
  - Keyboard: never fed to ImGui in play, so a chat box cannot work.
  - Bag items are 1x1 only.
  - Equipped items alias a backpack slot.
  - Crit and type information never reaches the client's combat text.

## 1. Rendering stack

**Frame flow** (`Render.Hud.cs:84-121`, both): `DrawHUD` disables depth; crosshair; `BeginHudFrame` (ImGui IO, NewFrame); floating combat text; world actor labels; `DrawHudInterface`; `ImGui.Render` and `RenderImGuiDrawData`. `HudRenderer.cs` is a thin pass-through (`:24-59`, `:62-95`).

**Custom path** (`Render.Hud.cs:26-46`, `:48-71`, `:259-276`; `HudLayout.cs:9-41`): a 2-float position VBO drawn as `PrimitiveType.Lines`. Crosshair only; no UVs, vertex colour or quads. Shaders `hudVertex.glsl`, `hudFragment.glsl`. Size `smallerAxis * 0.018` clamped 8..18 (`HudLayout.cs:18-19`). Hidden while an overlay is open (`Render.Hud.cs:261`).

**ImGui backend** (`Render.ImGui.cs`):
- Two contexts: play at `:177`; startup menu its own (`dream/Client/Startup/StartupMenuRenderer.cs:142-150`, with a duplicate `RenderImGuiDrawData`).
- Font: `io.Fonts.AddFontDefault()` at `:181` (ProggyClean, about 13 px). Atlas upload RGBA, Linear (`:193-211`).
- Shader: `hudVertexText.glsl`, `hudFragmentText.glsl` (`Frag_Color * texture`).
- **Texture per command:** `GL.BindTexture(Texture2D, (int)pcmd.TextureId)` at `:306`. Any GL handle passed as `IntPtr` will draw.
- **Scissor:** full support (`ImGuiScissorRect`, `:11-40`, used at `:296-307`).
- **`pcmd.UserCallback` commands are skipped** (`:295`), so `ImDrawList.AddCallback` does nothing and a custom orb shader needs a renderer change.
- 16-bit indices and no `VtxOffset`, so each draw list is limited to 64k vertices. `RendererHasVtxOffset` is never set.
- GL state is snapshotted and restored (`:45-170`, `:252`, `:317`).

| Primitive | In use today | Available from library | Notes |
|---|---|---|---|
| Solid rects, lines | ImGui widgets only; no `AddRect` calls | Yes | Crosshair is the only custom draw |
| Textured quads / Image | No | Yes (`ImGui.Image`, `ImageButton`, `AddImage`, `AddImageRounded`, `AddImageQuad`) | Grep of `dream/` finds none. Symbols confirmed by name in `ImGui.NET.dll`, not run |
| 9-slice | No | **No built-in** | Must be built from 9 `AddImage` calls |
| Atlas / sprite system | No | Font atlas only | No sprite table anywhere |
| Text | Yes | Yes | `TextUnformatted`, `HudText` (zero-alloc, stack UTF-8, `HudText.cs:13-59`), `ImDrawList.AddText` |
| Outlined text | Yes | Fake only | 4 offset copies plus fill (`Render.Hud.cs:156-160`, `:206-223`); no glow, no SDF |
| Scissor / clip | Yes, via ImGui | `PushClipRect` | Useful for vertical orb fill: clip a textured circle |
| Radial orb fill | No | Possible with `PathArcTo` and convex fill, or clip rect | No orb shader; callback skipped as noted |
| Render to texture | No | n/a | See section 6 |

**Fonts:** `dream/fonts/Arial.ttf` ships but no runtime code loads it (`AssetPaths.Font` is referenced only by `dream.Tests/Assets/AssetPathsTests.cs:30-32`). No `AddFontFromFileTTF`. `SetWindowFontScale` and `AddText(size)` scale the bitmap font, so large text is upscaled from a roughly 13 px atlas with Linear filter. Default combat text works out to about 50 px (13 × 1.65 × 2.35), crits about 67 px. Expect blur (inferred, not seen on screen).

**Scaling and resolution:**
- No UI scale setting. `FontGlobalScale` and `DisplayFramebufferScale` appear nowhere. `io.DisplaySize` is set each frame from `_currentSize` (`Render.Hud.cs:281-283`). HiDPI on a scaled Windows desktop is **unverified**.
- Default window 1440x1024 (`dream/Application/Game.cs:59`). Presets 1280x720, 1600x900, 1920x1080 (`Render.HudPanels.cs:215-229`).
- Most panel positions are hard-coded pixels: HUD (10,10) `:44`, Settings (20,70) `:96`, Build (10,300) `:278`, Inventory (20,420) `:416`. Slot buttons fixed 120x60 (`:557`).
- Only the loot feed and death overlay are viewport-relative (`:345-348`, `:362-365`), using `settings.WindowWidth`, not `_currentSize`.
- `docs/rendering-and-assets.md` (end of HUD Rendering, about line 221) asks that future placement come from viewport data rather than hard-coded pixels.
- Window positions persist in `imgui.ini` (gitignored, `.gitignore:407`).

## 2. What is on screen today

| Element | Branch | How it is drawn | Where |
|---|---|---|---|
| Red crosshair | both | Custom GL lines | `Render.Hud.cs:259-276` |
| Status panel "HUD" (top-left): HP, Mode, Damage, LVL, EXPERIENCE %, xp/next, Server, Players, rejection text, Resolution, optional perf lines, Esc/V hints | both | ImGui window of plain text. **No bars or gauges** | I `Render.HudPanels.cs:44-92`; M `:44` |
| World transfer line | both | Coloured text in that panel | I `:380-399`; M `:317` |
| Floating combat text | both | `ImGui.GetBackgroundDrawList().AddText` | `Render.Hud.cs:123-162` |
| Name and "Lv N cur/max" over actors | both | Background draw list, outlined text, no HP bars | `Render.Hud.cs:164-204`; `ActorWorldLabelBuilder.cs:84` (I) |
| Settings window (Esc) | both | ImGui sliders, including free Strength/AttackSpeed/Vitality dev sliders, combat text tuning, viewmodel rig, time of day | I `:94-233` |
| Inventory window (V) | both, differs | See below | I `:410-470`; M `:338-` |
| Build material panel | both | ImGui buttons 64x32 (Building mode only) | I `:273-321`; M `:264` |
| Loot feed | **I only** | No-input text window, at most 4 lines, 4 s each, at 68% height, **no rarity colour** | `:337-357`; `LootFeedbackSystem.cs:13-15` |
| Death overlay | **I only** | Scaled text "You died", respawn countdown | `:360-375` |
| Destroy confirm modal | **I only** | `BeginPopupModal` | `:444-467` |
| Startup and connect menu | both | Separate ImGui context | `StartupMenuRenderer.cs` |

**Inventory window, I:** sections "Character" (stat rows, base + bonus = effective), "Equipment" (four 220x32 text buttons), "Backpack" (4x4 text buttons, 120x60). Equipped items tinted green. Rarity text colours: Common default, Uncommon (0.35,0.90,0.40), Rare (0.40,0.62,1.00) (`:401-402`, `:514-524`). Tooltip (`:626-661`): coloured name, description, DetailLines, rarity label, signed modifiers, count; plain text; `MaxTooltipLines = 8` (`CharacterPanelModel.cs:56`). Right-click context menus offer Equip to each allowed slot, Unequip, "Move To" as a list of slot numbers, and Destroy (`:579-624`).

**Inventory window, M:** "Equipped Tool" button plus 4x4 "Backpack". Left click equips. Basic tooltip (`:419`). No context menu, no rarity.

**Not on screen on either branch:** target frame, minimap, buffs, party, quest log, shop.

## 3. Input

- `MouseState` is passed through `DrawHUD`; only `BeginHudFrame` feeds ImGui (`Render.Hud.cs:278-308`).
- **Mouse fed to ImGui only while an overlay is open** (`overlayVisible` = Settings or Inventory, `:285`). Otherwise `MousePos` is (-MAX,-MAX) and MouseDown false (`:287-291`). Only left and right buttons are forwarded.
- **No mouse wheel forwarded** anywhere in `dream/`.
- **No keyboard reaches ImGui in play.** `BeginHudFrame` has no `AddKeyEvent` or `AddInputCharacter`. `Game.OnTextInput` routes characters to `_startupScene` only (`Game.cs:327-331`). The startup menu handles keys (`StartupMenuRenderer.cs:233-256`); `ImGuiTextInputQueue` is reusable.
- **`WantCaptureMouse` and `WantCaptureKeyboard` are never read.** Gating is by `RuntimeMode`, not by cursor position.
- **Cursor modes:** `RuntimeState.DesiredCursorState`: Gameplay = `Grabbed`; InventoryOverlay and SettingsMenu = `Normal` (`RuntimeState.cs:7-17`), applied at `Game.cs:221`. `AllowGameplayInput` false in overlays (`PlayScene.Update.cs:28`). `io.MouseDrawCursor` on in overlays (`Render.Hud.cs:286`). Windows are `NoInputs` in play (`Render.HudPanels.cs:49`, `:282`). Consequence: **a bottom bar cannot be clicked in the FPS view.** Only one overlay shows at a time (`GameSettings.cs:85-94`, `:95-105`).
- **Click and drag:** ImGui clicks and context menus work. Windows move by title bar only; edge resize off (`Render.ImGui.cs:185-186`). **No drag and drop on either branch**; the API exists in the DLL.
- **`HudInteractionRequest`** (`HudInteractionRequest.cs`): per-frame immutable record of nullable requests, returned by `DrawHudInterface`, applied by `PlayScene.ApplyHudRequest` (`PlayScene.Render.cs:197`, `PlayScene.HudRequests.cs`). This is the right seam for new actions. I adds `InventoryAction: InventoryHudAction?` with kinds EquipTo, Unequip, Move, Destroy (`:43-61`), mapped to intents at `PlayScene.HudRequests.cs:136-142`. One inventory action per frame.
- **Key conflicts:** W/A/S/D, Space, LeftCtrl, F (freeze), D0 (noclip), **D1/D2 (Building/Combat mode)** (`PlayerController.cs:70-113`). Esc, V, C are polled directly and not gated for text focus (`PlayScene.Update.cs:107-130`). A 1-0 hotbar collides with D0, D1, D2. Right mouse is unused in gameplay. Bindings are hard-coded; no keybinding system.

## 4. Client data

**Inventory (both):** `InventoryConstants`: 4 columns, 4 rows, 16 slots, `EquippedSlotIndex = 100`. Snapshot carries `Columns`, `Rows`, `Slots`, `EquippedSlot` (`PlayerInventorySnapshot.cs`); I adds `Equipment`. `InventorySlotSnapshot(SlotIndex, Item, IsEquipped)`. **Strictly 1x1**: no width or height on item, definition or snapshot. Delivery: `JoinAccepted.Inventory`, `InventoryChangedResult`; I adds `InventoryPushMessage` (`server.inventory-push`, snapshot plus up to 8 `LootNotice`).

**Item snapshot:** M: id, kind, name, VisualId, StackCount, Description, DetailLines (`InventoryItemSnapshot.cs:3-10`). I adds Modifiers, HeldLight, EquipSlots mask, Rarity, IsRolled (`:11-23`). `StackCount` is always 1 in `GrantItem` on I (`PlayerInventoryService.cs:1085-1090`, approx.). No stacking.

**Equipment:** M: one slot (100, main hand). I: four slots MainHand, OffHand, Head, Body = 100..103 (`EquipmentSlot.cs:6-41`).
**Alias (I, H-D1):** an equipped item normally stays in its backpack row (`PlayerInventoryService.cs:31-34`). A classic paper doll that lifts items out of the bag needs a server rule change, or an accepted cosmetic hide. Equipped items cannot be destroyed. `worn.head` and `worn.body` visuals have no art (`items.json:109,124`).

**Item tiers:** I: `ItemRarity` Common, Uncommon, Rare only (`ItemRarity.cs`); `ItemRoll(Rarity, Modifiers)` fixed at creation (`ItemRoll.cs`); roll tables per rarity (`ItemDefinition.cs:8-23`); `RarityColorKey` has three values. M: none. **Tool tiers: none on either branch.** No item level, durability, requirements, socket or set. Content on I: 5 starter tools plus 4 rollable gear items (`dream/config/content/items.json`), loot tables for wolf and dire-wolf (`loot.json`), no consumables.

**Stats:** I: `StatId` = Strength, Agility, Vitality, Energy, AttackSpeed, MovementSpeed, Armor (`StatId.cs`), ops Flat and Percent. Live and shown: Strength, AttackSpeed, Vitality, Armor (`CharacterPanelModel.cs:74-85`). Armor applies via `100 / (100 + armor)` (`CombatFormulaService.cs:171`); the comment at `CharacterPanelModel.cs:83` ("no effect on combat") looks stale.
`HudStatusSnapshot`, both: CurrentHealth, MaxHealth, InteractionMode, Min/MaxDamage, effective Strength/AttackSpeed/Vitality, Level, Experience, ExperienceToNextLevel. I adds BaseStrength, BaseAttackSpeed, BaseVitality, Armor, IsDead, RespawnSeconds. Crit chance and damage exist in `PlayerCombatDerivedStats` but not in the HUD snapshot. Built in `WorldSessionPresentationState.cs:48`.

**HP and XP:** available and replicated (`ActorReplicationFrame.cs:45`; `ActorFrameJson.cs:27,174-176`). Formula `1000 + level * 23`. `ExperiencePercent` computed already (`HudStatusSnapshot.cs:65`).

**Intents:** M: `InventoryEquipIntent(SlotIndex)` only. I: `InventoryEquipIntent(SlotIndex, TargetSlotIndex?)`, `InventoryMoveIntent(From, To)` (swap), `InventoryUnequipIntent(SlotIndex)`, `InventoryDestroyIntent(SlotIndex, ItemInstanceId)`. Rejections are the `InventoryEquipOutcome` enum. Actions are budgeted (`TrySpendAction`, Throttled), which matters for drag spam. Not available: split, stack, drop to world (roadmap U7).

| Need | State |
|---|---|
| Multi-cell grid | Absent. Needs item W/H in `ItemDefinition`, `ContentCatalog` parsing, snapshot and wire; anchor-based placement in `TryMove`, `FindLowestFreeBackpackSlot` and loot grant; UI. `CharacterPanelModel.MoveTargets` clamps to 16. Grid size is compile-time. Wire-size tests exist (`WorstCaseInventoryWireSizeTests.cs`) |
| Paper doll | I has 4 slots. Ring, boots, gloves etc. need `EquipmentSlot`, `Count`, masks, `StatSheet` source slots (`EquipmentLoadout.cs:12`). Portrait needs render to texture or a static image |
| Gold | Absent (no code, config or SQL) |
| Mana | Absent. Only `StatId.Energy` (inert) and persisted `player_world_state.current_energy` (`0003_worlds.sql:56`), not replicated to HUD |
| Stamina | Absent |
| EXP | Present, both branches, text only |
| Skills | Absent. No casting, cooldown or ability protocol |
| Hotbar | Absent. Mentioned only in `docs/ledger/specs/2026-07-04-server-persistence-entity-hierarchy-and-denormalized-schema.md:97,227` ("slots 1..6") |
| Chat | Absent: no message type, UI or keyboard route. Needs a client message, server broadcast and sanitising |

## 5. Floating combat text

Same on both branches (`FloatingCombatText*.cs`, `CombatFeedbackSystem.cs`).

- **Styles** (`FloatingCombatTextStyles.cs:9-29`): Regular yellow-gold (1, 0.85, 0.05), scale 1.65, outline 1.75, priority 10. Critical red (1, 0.15, 0.15), scale 2.20, outline 3, priority 0. Incoming pure red, scale 1.3, outline 2, priority 50.
- **Layering:** sort is ascending, so **crits draw beneath regular text** (`FloatingCombatTextSystem.cs:115`).
- **Motion** (`FloatingCombatTextMotion.cs`): lifetime 1.0 s, burst 0.15 s (scale 0.2 to 1.1 to 1.0), ease-out rise of 1.0 world unit to 0.60 s, linear fade 0.60 to 1.0, depth pop during burst. Default tuning: size ×2.35, speed ×0.60, crit depth ×5 (`FloatingCombatTextTuning.cs:15-18`), so about 1.67 s real time. User-tunable and persisted.
- **Limits:** pool 150, max 4 per target, extras silently dropped (`FloatingCombatTextSystem.cs:10-11`, `:29-32`). Random X offset ±0.35. Anchor frozen at spawn.
- **Text:** digits only (`:43`). Amount ≤ 0 dropped (`CombatFeedbackSystem.cs:44`), so **no "MISS", blocks, heals, EXP or status text**. Text is not centred on the anchor (`Render.Hud.cs:156-160`).

**Match against colour-per-damage-type: poor.** `FloatingCombatTextKind` has 3 values and is hit severity, not damage type; there is no `DamageType` in code. `DMG_IMPL.md:20-29` specifies Regular orange #FF6600, Critical blue #0066FF, Excellent green, Ignore Defense cyan, Incoming red, Reflect purple, SD olive, Poison. 05 records where the measured look overrides these values. Code implements 3 classes with different colours and scales, and no glow.

**Crit and Incoming do not reach the client:** `ClientSnapshotDamageApplier.cs:44` and `ClientAuthoritativeActionApplier.cs:74` both publish `Regular`. Wire types carry no kind (`DamageEventDelta`, `CombatResolutionResult`). `FloatingCombatTextKind.Incoming` is never constructed outside the style switch and tests. As far as traced, every number in a client session is gold. **Not fully traced:** whether the resolver's `Critical` publish (`AuthoritativeCombatResolver.cs:268`, built at `WorldSessionRuntime.Combat.cs:62`) can reach the client hub in some in-process path; confirm with a run.

**Needed for parity:** a kind field on the wire plus a protocol bump, a client decision on "incoming", more enum and style rows, and outline/glow via offset copies or a bitmap number font.

## 6. Item icons

- **No item icons on either branch.** `VisualId` is the only visual key, used only by viewmodels (`ViewModelAssets.cs:30-71`; `PlayScene.Replication.cs:284,306`).
- **All tools use one placeholder model**, `viewmodels/DebugHeldItem.gltf`, with different offsets (`ViewModelAssets.cs:30-77`).
- **Unused source meshes exist:** Quaternius `fantasy-props-megakit`, `survival-pack`, Kenney `food-kit` (`dream/animations/library/`). Kenney food-kit has `Previews/*.png`, usable as food icons. Licences per `library/README.md` (not read). No UI, icon or button packs seen (file names only).
- **Render to texture: not available.** No FBO code in `dream/`. An icon baker or portrait needs new FBO code plus lighting via `FrameLightingBuffer`. Baking icons offline to PNG is the simpler route.

## 7. Asset pipeline

- `docs/rendering-and-assets.md` has no section on UI textures, atlases or fonts. "Texture Handling" (line 288) covers glTF only.
- `AssetPaths` resolves from `AppContext.BaseDirectory`: `shaders`, `animations`, `fonts`, `config` (`AssetPaths.cs:10-26`). No `textures` or `ui` folder.
- `dream.csproj` copies those four folders with `<None Update ...>`, `PreserveNewest`. A new root needs adding and checking.

**Adding an atlas (no code exists yet):** add `dream/ui/` plus csproj glob and `AssetPaths.Ui()`; decode with `TextureDecoder.DecodeRgbaFlipped` and upload with `UploadTexture2D` (`TextureDecoder.cs:17,34`); pass the handle to ImGui as `IntPtr`.

Traps: `DecodeRgbaFlipped` flips vertically, so invert ImGui UVs or add an unflipped decode. `UploadTexture2D` sets no wrap mode (GL default Repeat) and only Linear or mipmap filters, so atlas edges can bleed and pixel art gets no Nearest; add a variant with ClampToEdge and chosen filter. A sprite coordinate table needs a data format. Dispose via `Render.Lifetime.cs` (not read in detail).

**Adding a font:** `io.Fonts.AddFontFromFileTTF(AssetPaths.Font(...), size)` before `CreateFontTexture` in `InitializeImGui` (`Render.ImGui.cs:181-188`). Multiple sizes mean multiple baked entries. Default glyph range is Latin. The startup menu context needs its own call.

## 8. Gap table

S = up to days, M = a week or two, L = multi-week or crosses client, server, wire and persistence. Rough estimates for one implementer.

| Feature | Exists? | Branch | Effort | Notes |
|---|---|---|---|---|
| Textured UI primitives (image, 9-slice, sprite table) | Partly: backend ready, no helper | both | M | Wrapper over `ImDrawList` |
| UI texture loading and atlas | No | neither | S | Variant upload; UV flip |
| Custom font loading | No (Arial unused) | neither | S | |
| UI scale and anchored layout | No | neither | M | Fixed pixels today |
| Cursor or click mode in play | No | neither | M | New RuntimeMode or held key; `WantCaptureMouse` gating |
| Keyboard and text into ImGui in play | No | neither | M | Reuse startup code; gate V, C, Esc, movement |
| Mouse wheel | No | neither | S | |
| Bottom bar frame | No | neither | S-M | After primitives |
| HP orb / gauge | Data yes, art no | both | S | |
| Mana orb | No data | neither | L | Needs a resource system, or placeholder |
| Stamina / AG gauge | No | neither | L | Same |
| EXP bar | Data yes | both | S | |
| Hotbar visual | No | neither | M | |
| Hotbar assignment and use | No | neither | L | Persistence, usable actions, rebinding 1, 2, 0 |
| Skills | No | neither | L | Whole system |
| Chat display and input | No | neither | L | Protocol, server, limits, input focus |
| Paper doll 4 slots | Data and intents yes; text buttons | I | M | Alias decision first |
| Paper doll more slots | No | neither | M-L | Enum, stat sheet, content, tests |
| Character portrait | No | neither | M-L | FBO or static art |
| 1x1 icon bag grid + drag and drop | Move intent yes; no DnD or icons | I | M | ImGui DnD plus `InventoryHudAction` |
| Multi-cell grid | No | neither | L | Server, wire v12, loot, migration check |
| Larger bag | No | neither | M | Constants pinned in several places |
| Gold display | No | neither | M-L | Currency, persistence, loot |
| Item icons | No | neither | M | Art or offline bake, `VisualId` to sprite map |
| Rich tooltip layout | Text tooltip yes | I | S-M | Add compare, stat colours |
| Item tiers beyond 3 | 3 rarities | I | M | Enum, roll tables, colours, wire value |
| Tool tiers / item level | No | neither | M-L | Design needed |
| Loot feed rarity colours | Plain lines | I | S | Store key with the line |
| Combat text restyle | Basic | both | S | Colours, scale, layering fix |
| Combat text crit and incoming on client | No | both | M | Wire field, protocol bump |
| Combat text damage types, miss, heal | No | neither | M-L | Depends on design |
| Actor HP bars | Text only | both | S | |
| Render to texture | No | neither | M | Only if portrait or live icons wanted |
| FBO icon bake tool | No | neither | M | Needed by the icon recipe in 02; offline bake to PNG is simpler than live |
| Custom UI shader path | No (`UserCallback` skipped at `Render.ImGui.cs:295`) | neither | S-M | Needed for per-item tint and glow in 02 and two-channel digits in 05 |
| Nearest-filter upload | No | neither | S | Needed by 05's digit atlas and any pixel art |
| Custom cursor | ImGui cursor only, in overlays | neither | S | Gauntlet cursor in 01 |
| System notices | Loot feed only | I | S-M | Time-stepped gold list in 03 |
| Combine panel | No | neither | L | Recipe system, server rules, gold |
| Coordinates plate | No | neither | S | Position data exists client-side (not verified) |
| Item durability and requirements | No | neither | M-L | Tooltip rows in 02 need the data |
| Rarity recolour | Uncommon green, Rare blue today | I | S | 02's tier table sets Uncommon rose `#F08FB0`, Rare green, top tier per that table |

**Suggested order of work:**
1. Merge or base on I, so rarity, equipment and intents are available.
2. Decide art direction and asset source. Add UI folder, atlas loader (flip, clamp, filter) and fonts.
3. Build a small UI layer over `ImDrawList`: sprite, 9-slice, gauge, anchored layout, UI scale.
4. Solve input: click mode in play, `WantCaptureMouse`, keyboard and wheel routing, key conflicts with 0, 1 and 2.
5. Reskin existing data: status bar, HP orb, EXP bar. Show mana and skill areas empty or hidden until systems exist.
6. Reskin inventory on I with 1x1 icons, drag and drop to `Move`/`EquipTo`/`Unequip`, and the rich tooltip. Decide the equipped-alias rule here.
7. Fix combat text: first the wire kind, then styles and layering.
8. Icons: bake or commission, and map `VisualId` to sprite.
9. Then the system features, each its own design: chat, mana and skills with hotbar, gold, multi-cell grid, extra equipment slots, item tiers.

## Not verified

- Nothing was run or built; on-screen claims come from reading code.
- HiDPI behaviour, and blur of scaled text.
- That the named ImGui.NET APIs work here; only the symbols were confirmed in the DLL.
- Crit delivery on the in-process path (section 5).
- `RuntimePersistenceBridge` location on I, and whether `Energy` is saved with real values.
- `Render.Lifetime.cs` and asset licences were not read.
- The `GrantItem` line range is from a partial read.
