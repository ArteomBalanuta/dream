# Isometric view for dream: feasibility report

## Recommendation (read this first)

**Go, in phases, with a hands-on decision after the first phase.**

- **It is not a rewrite.** The server, the simulation, physics, persistence and tick determinism barely change. The spike needed no server edits, and most of the protocol can stay as it is.
- **The camera is the cheap part.** The first run of the camera worked with no renderer changes, and it was slightly cheaper to draw than today's view.
- **The biggest engineering problem is occlusion.** In a town of 4-high walls the player simply disappears. That is a new system, but my spike showed it can be solved.
- **The biggest overall gap is art, not code.** The world and models were built for close-up viewing. From above, the flat cube world, the near-black wolf and the character with no visible weapon or attack animation are all exposed.
- **Rough cost:**
  - Minimum: 1 small wave.
  - Proper: 1 large wave.
  - Full: 1 large wave.
  - Art: open-ended, and not an engineering task.
- **Do first:** a small wave that ships the camera, cursor aim, fog fix, antialiasing and a blob shadow. Then you play it for 30 minutes and decide before paying for anything else.

## 1. What the view in the screenshot is

**Read from the image, by eye only (I fitted no lines):**
- It is a **fixed high-angle perspective camera, not true isometric**. Parallel wall lines drift, pillars lean outward toward the screen edges, and near objects are visibly larger.
- Pitch is about 45-50° below horizontal, from the top faces of the pillars. Field of view looks narrow, around 30-40°.
- The grid runs diagonally across the screen, so the camera sits at about 45° yaw.
- The hero's body is about 16% of screen height. In our world that matches a camera 11-13 m away, showing about 12 m by 12 m of ground.
- The label shows integer tile coordinates, "Devias (225, 220)", so it is a tile-based map. Devias suggests MU Online or a private server of it.
- Tall walls sit on the far side of the walkable area. I see no clear cutaway. An authored map avoids occlusion by level design, and ours cannot.
- HP bars float over units, item labels sit on the ground, and characters have dark ground shadows.

**Cannot be read from a still image:** whether the camera rotates, and how selection works. From general memory, not from the image, that game uses click-to-move and click-to-attack.

**Orthographic versus fixed-angle perspective in our engine (both ran):**

| | Perspective (35° FOV) | Orthographic |
|---|---|---|
| Culling, labels, damage numbers | worked unchanged | worked unchanged |
| Look | closest to the screenshot, walls lean | very clean, true isometric |
| Reading height | small depth cue | a block one up and one back overlaps almost exactly |
| Picking | same inverse-matrix code | same inverse-matrix code |
| Near-plane cuts | none seen | none seen |

Use perspective as the default. Both go through the same code path, so the choice costs nothing and can change late.

## 2. Every system the change touches

**Severity scale:** none, small, medium, large, rewrite. Nothing rated rewrite.

| System | Severity | Why |
|---|---|---|
| Camera and view matrices | small | `ThirdPersonCameraRig.cs:26-46` is the single seam, called from `ClientWorldSession.cs:148`. Every renderer already reads `presentation.Camera`. |
| Frustum culling | none | Plane extraction works for any projection (`ViewFrustum.cs:56-74`). Verified with ortho. |
| Projection | small | Single perspective matrix at `Render.cs:100-104`. Needs per-mode near/far. The world viewmodel has its own. |
| Chunks, draw calls | none, slightly better | See the perf table in section 3. |
| Fog | small, **broken at once** | Fog measures from the camera (`lighting.glsl:18-22`). At 22 m the top of the frame is milky. At 60 m the whole world vanished. Measure from the player instead. The fog-bounds tests move. |
| Sky | small | A 48° pitch never sees it, except past the map edge, where a bright void appears. The edge needs treatment. Sun, moon and stars are lost; lighting still carries day and night. |
| Day and night | none | Torches read very well from above. This was the best-looking frame. |
| Shadows | medium | None exist at all. Actors look like they float. Blob shadow is small; a shadow map is medium. |
| Antialiasing, filtering | small | No MSAA and no anisotropic filtering (`TextureDecoder.cs:78,110`). Every world edge is now diagonal, so jaggies are obvious. |
| Side-face lighting | small to medium | Faces away from the sun go nearly black, and two side faces are always visible. Needs ambient retuning or voxel AO. |
| Viewmodel | none | Already skipped in third person (`PlayScene.Render.cs:167`). No new investment. |
| LOD | none | The view is bounded, so this is not needed. |
| Character and wolf models, animation | large (art) | `PlayerRenderState.cs:49` has only idle and walk, with no attack clip. There is no weapon on the world model. The wolf reads as a black shape. |
| Occlusion | large (new system) | Details below. |
| Input with WASD plus mouse aim | small | Proven, no protocol change. Details below. |
| Pointer and cursor | medium | Mouse is grabbed by `RuntimeState.cs:16`. HUD input is interactive only with the inventory open (`Render.Hud.cs:233`). Left click reaches the attack whether or not it was on the HUD (`PlayerController.cs:111`). Needs a click-capture gate. |
| Click-to-move | large | Details below. Client-only steering avoids new authority. |
| Combat | small | Details below. |
| Building | medium, needs protocol v13 | Details below. |
| Physics, jumping | none to small | Follow the player's Y with smoothing, or jumps bob the camera. A blob shadow is needed to read jump height. |
| Networking | none for camera and cursor | Client-only. Cursor aim adds about 10 B per snapshot because the player is always "turning" (estimated from the docs, not measured). Building aim needs protocol v13. |
| HUD | small to medium | Classic HUD looks right in the frames. Labels and damage numbers projected correctly. Crosshair must go. Sprite cursor must be always on. V no longer changes the cursor state. |
| Audio | none | There is no audio code in the project. |
| Tests, goldens | small until pathing | Camera and cursor do not touch simulation. Tests on `CameraViewMode` and fog bounds change. Enemy pathing is what moves tick goldens. |

**Occlusion, verified.**
- A 4-high wall hid the player completely, and so did a 2-wide alley. Frames: `captures\wall-persp\01-play.png`, `captures\alley-plain\01-play.png`.
- At 48° pitch, a wall hides the player for up to (height − 1.2 m) / 1.11 behind it. World height is fixed at 8 (`WorldDescriptor.cs:17,61`), so the worst case is about 5 m.
- **Good news: player-built roofs cannot exist today.** A cube placed in mid-air through the real build path fell from about 3.1 to 1.5 in under a second, and my test roof collapsed. Generated terrain is at most 2 cells high, with no caves. So today's problem is walls and towers, not ceilings.
- That advantage disappears if you later want static roofs, caves or taller worlds.

| Option | Result in the spike | Cost |
|---|---|---|
| Silhouette (draw hidden player bright, depth test reversed) | Worked, clearly visible through a wall, 2 extra draws. Needs a distinct colour: untinted, it looked like the player stood on the wall. | small |
| Shader cutaway (discard between camera and player) | Worked, player and floor visible. Rough rim, and it exposes the inside of cubes. Reads as a bite out of the wall. | medium, to make clean with caps |
| Fade, or hiding whole cells above the player | Not tried | medium |

**Input, verified.** Two facts decide the whole design:
- The client already sends a world-space `Facing` in every command (`RemotePlayerCommandRelay.cs:20`), and the server trusts it (`WorldSessionRuntime.Commands.cs:284-290`).
- Move axes are read relative to that facing (`PlayerMovementResolver.cs:79-83`) and travel as full floats.

So the client can aim at the cursor and rewrite the axes, and the server sees ordinary commands.

**Self-test results.** The test ran on loopback with a synthetic cursor spinning the facing once every 1.5 s, while walking a 1 s per side square:
- The path returned exactly to (24, 1.5, 36).
- Maximum sideways error was 0.1 mm.
- Over 190 server comparisons had completed by the end of the walk, with **0 corrections** and a maximum error of 0.1 mm.

So click-to-move is not required. WASD plus mouse aim works with no protocol change. Tick goldens hold because the server logic does not change.

**One caveat, untested.** The move axes now change every tick, so `BufferedPlayerCommandInbox.HasSameMovement` (line 294) will see "different movement" during network stalls. Run the wave 9 stall scenarios, and consider a world-space move field later.

**Click-to-move needs:**
- A navigation layer over a world that changes and has pushable cubes.
- A follower that turns a path into per-tick commands.
- Auto-jump, because the player has no step-up. A 1-cube step needs a jump, and jump height is about 1.27 m.

**My design advice is to steer on the client:**
- The client computes the path and sends ordinary WASD-style commands, plus jump.
- There are no new intents and no new authority, and the cheat surface is the same as today.
- Prediction and goldens are untouched.
- If a cube moves, the follower bumps, notices no progress and re-plans.
- The server needs its own pathing only for enemies.
- A 1 m box did pass a 1-cell door. Centred it passed cleanly. Offset by 8 cm it passed with 5 small corrections, up to 0.18 m. My guess is it brushed or shoved the door jamb; I did not verify that.

**Combat.**
- Attacks are synthesized on the server from the held button, using eye position and facing, with a 4 m range and a 0.9 m ray radius (`WorldSessionRuntime.Commands.cs:300-315`, `CombatUseResolver.cs:10`, `EnemyRegistry.cs:118`).
- A cursor-derived facing is the same input. The `fight` script run in iso killed a wolf through the real path, using its own facing input; the cursor path itself never attacked.
- Melee has no line-of-sight check: enemies are resolved before cubes (`AuthoritativeCombatResolver.cs:149-165`). That matters more when you can click a wolf behind a wall.

**Building.**
- Placement is always eye + facing × 2 m (`PlayerInteraction.cs:11,72-73`), and the server allows up to 3 m (`WorldSessionRuntime.Building.cs:13,26`).
- You cannot choose a cell from above with facing alone. By my geometry, a ground cube always lands about 1.8-2 m from the player.
- The client cannot send its own target, because `BuildUseIntent` is rejected on the wire (`PlayerActionHandler.cs:78-82`).
- This needs protocol v13: an aim point on the command, a face-based cell pick, and a ghost preview.
- The Y axis is then handled by clicking a face. Stacks read well from above: see `captures\tower-height\01-play.png`.

**Enemies.** They walk straight lines and stop at the first blocked sample, with no sliding and no change of Y (`EnemyActor.cs:128-158`, `EnemyMovementCollision.cs:49-71`). That confirms the gap you described.

## 3. The spike

**State:** finished and committed to my worktree branch only, `worktree-agent-af65f49bca6e13ac0`, commit `4279e89`. Not merged, not pushed. 12 files, +423 lines, most of it test probes. Everything is off unless `DREAM_ISO_CAMERA` is set; `IsoCameraSpike.cs` lists the flags.

**Measured** at 1440x1024, uncapped, at noon, in a town of 2,880 placed cubes. Medians from the `[render-perf]` lines. The cutaway and silhouette run also held these figures.

| Camera | FPS | Draws | Triangles | Chunks | GPU world |
|---|---|---|---|---|---|
| First person | 568 | 62 | 82,900 | 9 | 0.17 ms |
| Third person | 524 | 73 | 84,330 | 10 | 0.18 ms |
| Iso perspective, 16 m | 556 | 42 | 25,498 | 6 | 0.13 ms |
| Iso orthographic | 781 | 44 | 26,232 | 6 | 0.11 ms |
| Iso perspective + cutaway + silhouette | 775 | 48 | 25,498 | 6 | 0.09 ms |

- The open-ground scene gave the same pattern, at about 1,750-2,200 FPS on all cameras.
- FPS differences here are mostly noise. Frame time is dominated by CPU work that is the same in every mode.
- The solid result is about 30% fewer draws and about 70% fewer triangles.
- These numbers are from a very strong machine (the PC has an RTX 4090). I did not confirm which GPU the game used, and I have no low-end number.

**Broken at once:**
- Fog.
- The void at the map edge.
- The crosshair sitting on the player (I hid it).
- No shadows.
- Jaggies.
- Near-black side faces.
- Moiré stripes on the micro-cell pyramid.
- Thin black seams between physics cubes, seen in `captures\house-persp\01-play.png`.
- Black dark-side wall faces, seen in `captures\open-persp50\01-play.png`.

**Not broken:** culling, near plane, labels, damage numbers, third-person player drawing, night lighting.

**How the world reads:** better than I expected for structure. Rings of walls, stepped towers and the whole-map view are clear. It reads as a clean voxel game, not as the screenshot.

**Filtered tests with the spike in tree:** 102 of 102 passed (tick equivalence, state checksum, third-person rig, reconciler, frame lighting contract, frustum). No full-suite run.

**Captures** are under `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\`, logs under `...\artifacts\iso-spike\logs\`, and the run build under `...\artifacts\iso-spike\build\`. Each frame is below in full:

- Perspective, reference-like distance: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\open-persp-close\01-play.png`
- Orthographic: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\open-ortho\01-play.png`
- Whole map from above: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\town-overview\01-play.png`
- Fog failure at 60 m: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\overview\01-play.png`
- Night with torch: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\night-persp\05-snap.png`
- Player hidden in an alley: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\alley-plain\01-play.png`
- Same alley, silhouette: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\alley-silhouette\01-play.png`
- Same alley, cutaway: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\alley-cutaway\01-play.png`
- Fallen roof: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\house-persp\01-play.png`
- Stepped tower: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\tower-height\01-play.png`
- Wolf close up: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\wolf-close\02-snap.png`
- Third-person baseline: `G:\workspace\projects\dream\.claude\worktrees\agent-af65f49bca6e13ac0\artifacts\iso-spike\captures\base-third-person\01-play.png`

## 4. Phases

**Sizes:**
- Small: one implementer, no architect.
- Medium: architect, 2-3 workstreams, a fix loop.
- Large: like the classic HUD wave, about 5 workstreams and 2 fix loops. The diff from `1aee5f0` to `e6304a1` is 186 files and 28.6k insertions, including UI assets.

The sizes below are my judgement, by analogy with those waves.

**Phase 1, Minimum: small wave. No dependencies.**
- `CameraViewMode.Isometric`, a real camera rig, per-mode projection, Y smoothing.
- Fog from focus, MSAA and anisotropic filtering, map-edge background, blob shadow, no crosshair.
- You asked for "camera with today's controls". That part alone is smaller still, but I advise against stopping there. Today's controls turn the character while the camera stays fixed, and W follows facing rather than the screen.
- Cursor aim was proven in about 100 lines, so include it, plus the HUD click gate.
- **Smallest proof:** you play it for 30 minutes.

**Phase 2, Proper: large wave. Depends on Phase 1 and your go-ahead.**
- A. Pointer and input scheme, world picker with cell and face results, hover targets (medium).
- B. Combat and build targeting: protocol v13 aim point, ghost preview, line of sight for melee (medium).
- C. Occlusion: silhouettes for all actors, clean cutaway, tested against arbitrary structures (medium).
- D. Presentation: attack, hit and death clips, weapon sockets, replicated attack event, AO or shadow map (medium).
- **Smallest proof:** build a walled house from above and fight a wolf inside it.

**Phase 3, Full: large wave. The nav core depends on nothing else.**
- Nav core (medium).
- Client path follower with auto-jump (small to medium).
- Enemy pathing (medium).
- The nav core is worth doing whichever view you choose, because enemy pathing is an open gap. Enemy pathing could ship before Phase 2.
- It must reconcile with the unfinished wave I fix loop 5 work in worktree `agent-ae7c5a6c63e801461`.
- **Smallest proof:** a wolf walks around a wall the player just built.

**Phase 4, Art: open-ended.** Start with a small look-development slice using one better character and one better terrain pass.

**Reusable even if iso is dropped:** camera rig, picker, nav, MSAA and filtering, AO, shadows, silhouettes, line of sight, world-model animation and sockets.

**Thrown away:** the spike code itself. `CursorChangeLookHoldoff` and part of `GameplayInputResumeFilter` become unnecessary in iso.

**Unlocks:**
- Enemy pathing.
- Larger worlds, because the view is bounded.
- More enemies on screen.
- Ground-targeted skills.
- A good fit with the classic HUD.

**Blocks or makes expensive:**
- Multi-storey interiors, caves, and worlds taller than 8.
- New first-person investment.
- Exploring by looking around.

## 5. Robustness

**What makes it solid rather than a hack:**
- **One camera abstraction** that returns view and projection together, plus one input scheme interface. Both modes produce the same `PlayerCommandFrame`, so the server never knows which view is in use.
- **One picker in Simulation**, shared by client and server, so a cell is judged the same way on both sides.
- **Navigation:**
  - Keep a per-column bitmask of standable levels. At 100 x 100 x 8 it is tiny.
  - Update it at the choke point the map already has: every write to the geometry and rubble stores goes through the checksum helpers (`Map.Checksum.cs:5-7`). The same point works on client and server.
  - Treat partial cells as blocked. Ignore rubble, because it does not collide with players.
- **Determinism for enemy paths:**
  - Integer costs and an explicit tie-break (cost, then heuristic, then cell index).
  - No dictionary-order dependence and no allocation.
  - A per-tick budget, served in actor-id order.
  - Keep a straight-line shortcut when the way is clear, to limit golden churn.
- **Occlusion:** driven by a test, not by level design.

**Top risks, ranked, with how to test each:**
1. **Player hidden by arbitrary structures.** Test: generate random structures, cast rays from camera to 5 points on the actor, assert each is visible or covered by the cutaway or silhouette, plus golden frames through `DREAM_HUD_CAPTURE`.
2. **The world does not look good enough from above.** Not testable by machine. Use a look-development slice and your judgement before Phase 3.
3. **HUD clicks leaking into the world, or the reverse.** Test: scripted pointer through the real router, asserting no primary use on any HUD click, across inventory, settings and focus changes.
4. **Stale navigation in a physics-driven world.** Cubes fall and can be pushed. Test: property test that incremental updates equal a full rebuild after random edits, plus a soak with cube pushing.
5. **Enemy path determinism.** Test: two lockstep servers with terrain edits and restarts, same harness as the wave D and E lockstep tests.
6. **Building from a fixed angle.** Hidden faces, reach. Test: scripted placement on every face, including behind a wall with the cutaway on.
7. **Cursor aim under network stalls.** Test: the wave 9 stall scenarios with facing spinning.
8. **Two modes drifting apart.** Test: run a shared scripted scenario in both modes.

**Should first person stay?** Keep it as a frozen option, with no new viewmodel work. The code cost of both is low, because the camera and input abstraction hide it. The doubled cost is in art and balance: close-up detail and long-distance readability pull in different directions. Decide finally after Phase 2.

## 6. Art and content

**At this distance, acceptable needs:**
- **Character:** a less blocky model, or at least a weapon on the hand. Clips for attack, hit and death. Smoothed turning, because cursor facing snaps instantly. Gear should show on the model, or the new equipment is invisible.
- **Wolf:** a much brighter texture or a rim light. It is currently a black shape. `Rendering/Actors` already has tints, and the Quaternius animal and character packs are already in the repo.
- **Terrain:** rectangular material zones and visible texture repetition are the main problem, more than cube shape. Blended material edges, AO and small scatter props would help most.
- **Cubes:** merged walls have no visible block edges, so large walls read as flat slabs.
- **Effects:** there are no particles or glow to speak of. Most of the screenshot's excitement is weapon glow and hit effects.

**What a cube world cannot look like:**
- The screenshot has authored, ornamented walls, doors and banners, and painted ground transitions.
- A 1 m grid with one texture per cell will not match that. It can look like a good stylised voxel game, and the top-down frames suggest that is a real look.
- You also get something an authored map cannot: players build and destroy the world, and the world looks fine from all four sides, so a 90° camera turn is possible.
- Static prefabs from the prop library could add authored detail, but they would not be destructible.

## 7. Decision

**Go in phases. Do Phase 1 first, as a small wave, then stop and play it.**

**What would change my mind:**
- **Stop** if it does not feel good in your hands after Phase 1. You will have spent only a small wave.
- **Reconsider** if the game's future needs real verticality: interiors, caves, taller worlds. Occlusion cost rises sharply.
- **No-go** if the game's identity is precise first-person aiming.
- **Scale down the ambition** if no art budget exists. Then aim for a stylised voxel look, not the screenshot.

## Not determined

- **Nobody has played this by hand.** All evidence is still frames and scripted input, so I cannot judge feel, motion, camera comfort or edge crawl.
- Cursor aim used a synthetic cursor, and never fired an attack.
- Performance on weaker GPUs.
- Multiple clients in iso, and real network conditions.
- The roof result: live placement was tested for one cube, and restored roofs were seen collapsing in one scene. Those two observations are the evidence.
- The art judgements are mine, from stills.

## Side findings (none fixed)

- **Floating plate.** `WorldGenerator.cs:293` has `nearPeak` with no x/z bound. That creates a 10 x 10 m thin plate at the pyramid's top, visible even in the third-person baseline. In iso it hides whatever is under it. It caught me out at first: the "flat plane" in my early town frames was this plate, and the town perf rows I quote use a clear alley instead. Fixing it changes the map, so golden tests would move.
- **Duplicate JSON key.** `PlayerCommandJsonConverters.cs:300` writes `freezePressed` twice, so the key is duplicated in every command. Harmless.
