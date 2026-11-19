# Work log: sculpted equipment

- Purpose: worn armour, weapons and equipment read as blocky and Minecraft-like. Make them sculpted,
  gothic and materially distinct, without touching the camera, the base figure, the physics or the
  ImGui overlay.
- Status: on `integrate/sculpted-equipment` from master `011eaca7`. Not pushed, not merged.
- Method: the swarm workflow in `CLAUDE.md` (frozen contracts, three parallel implementers,
  integration, review, one art fix loop), with the discipline in the `software-engineering` and
  `parallel-agent-delivery` skills.
- Plan: `docs/ledger/plans/2026-11-19-sculpted-equipment-plan.md`.

## The gap this closed

Every weapon, shield and armour piece was a handful of **axis-aligned unit cubes**, drawn as batched
unit-cube draws of one shared mesh, through a purely diffuse actor shader with `metallicFactor 0` and
`roughnessFactor 1`. So blackened iron, oiled leather and polished steel differed only in how dark
they were, every edge was 90 degrees, and every surface was one flat value. The baseline captures in
`artifacts/captures/equip-before/` show it plainly: a stack of rectangles.

The 2026-11-05 wave concluded that "at 28 px a loft-smooth attachment buys nothing visible" and
authored plain boxes on purpose. **This wave deliberately reverses that call**, on a narrower
argument: curvature is a *shading* change, not an ornament change. A curved pauldron gives a gradient
where a box gives one flat value, and a gradient reads at any size. Fine ornament below the pixel
rate is still wasted work, and this wave found that the hard way too (see "grain" below).

## What landed

- **`EquipmentShape` + `EquipmentShapeLibrary`** (new): twelve sculpted unit meshes — bevelled box,
  shaft, taper, blade, plate shell, dome, spike, ring, crescent, gem, cloth panel, and the legacy
  box. Each is built inside the frame the old cube used, with outward winding, split normals at a
  facet and averaged normals around a curve, UVs in `[0, 1]`, and is then fitted to that frame so a
  part's authored `Size` is its real extent. 12 to 260 triangles each.
- **`WeaponPart` / `ArmourPart`** gained `Shape` and `YawDegrees`, both defaulted, and `ToMatrix`
  applies yaw after roll. `Box` is unchanged, so the change was a no-op until the catalogs moved.
- **`EquipmentSurface`** (new): `Metallic`, `Roughness`, `Grain` and `AccentMix` per weapon and
  armour material, and **`EquipmentTint` / `EquipmentTintCatalog`** (new): five tiers, a primary and
  an accent multiplier, applied on the CPU to the part's `baseColorFactor` and clamped under the
  albedo ceiling. **`EquipmentMeshCache`** (new) holds one `StaticMeshBuffer` per shape, built lazily.
- **`enemyFragment.glsl`**: one block after the brightness floor, gated on `uEquipSurface.y > 0.0` —
  normalised Blinn-Phong specular against sun, moon and point lights, a fresnel edge sheen and a
  procedural surface grain. `dreamPointSpecular` added to `pointLights.glsl`. With the uniform at
  zero the shader is line-for-line what it was, and `enginelook.py`'s 20 pinned lines are untouched.
- **Both renderers** draw each part with its shape's mesh, set the surface per material, apply the
  item's tint, and reset the uniform in a `finally`. `AnimatedModel.BindProgram` zeroes both new
  uniforms on every program switch, because the MASK variant is its own program object.
- **Both catalogs re-authored**: 45 models (23 held, the generic stand-in, 21 worn). **No part uses
  `EquipmentShape.Box` any more**; genuinely rectilinear things are `BevelBox`. No visual id, grip,
  mount, `IsShield`, `IsRanged` or palette entry changed, and `HeldItemScale` is untouched.
- **`check_char.py`** parses the new named arguments and applies yaw the way `ToMatrix` does.

## What the engine found that no test could

Every one of these came from looking at a real frame, not from a diff.

1. **The first pass washed the whole figure to cream.** The Blinn-Phong lobe was unnormalised, so a
   rough material (leather at roughness 0.8 resolves to a shininess near 2.5) got a lobe nearly as
   wide as the diffuse term at full amplitude — a second diffuse light, not a highlight. Normalising
   by `(shininess + 8) / (8 pi)` made rough read as a dim broad sheen and polished as a tight glint,
   which is the distinction the material table exists for. Strength then settled at 0.30.
2. **The grain was invisible because it was below the pixel rate.** It started at 11, 38 and 72
   cycles per world metre. One pixel spans about 0.02 m on a worn piece at the shipped zoom, so the
   fine octaves put one or two whole cycles inside a pixel and averaged to flat grey. Dropped to 4,
   11 and 26, where the coarse octaves read as dents and soot across a few pixels.
3. **`Plate` was drawing in the front 41% of its box.** It shipped as a 150-degree arc of outer
   radius 0.5, so its Z ran `[0.09, 0.50]` and its centroid sat a third of a box forward of the
   part's `Center`. Plate armour missed the back of the body and the boot shaft sat in front of the
   ankle — and **no gate could see it**, because every cover gate measures the part's *box*. Found by
   the reviewer, visible in the captures as a cuirass that read as a flat slab from above. `Plate` is
   now a 230-degree shell and every shape is fitted to its unit box, which also recovers the ~5% a
   polygon loses between its vertices. `EquipmentShapeLibraryTests` now pins each shape's real AABB.
4. **The values were wrong even though the palette was right.** The camera is pitched 50 degrees
   down, so an up-facing normal takes the sky ambient before any light reaches it. Every level
   `Steel` or `Bone` surface was therefore the brightest thing in frame: the legs read as a stack of
   pale cream hoops (a `Ring` lies in the XZ plane and shows its whole top), the helms as near-white
   domes, the cloaks as one flat slab across the lower body. The fix was placement, not palette —
   light materials off the large up-facing surfaces and kept for rims, bevels, brow bars and cutting
   edges; one bright band per limb piece; cloaks re-hung as narrow mirrored falls with a stepped hem.

## Decisions, and the evidence behind them

- **Sculpted primitives in C#, not generated glTF attachments.** The attachment path already works
  and is proven; the gap was the mesh, not the rig. A shape library keeps the "a new item costs no
  asset and no GL object" property, keeps everything deterministic and testable without a GL context,
  and avoids the normalisation and parenting story the glTF loader does not expose.
- **One gated uniform, not a second shader program.** `uEquipSurface` zero is a provable no-op, which
  is cheaper to guarantee than a parallel program and keeps every pinned contract line intact.
  `FrameLightingContractTests` was tightened back into a single contiguous match so a *second*
  post-floor block cannot slip in beside it.
- **A specular highlight is allowed to clip to white.** This is the one deliberate exception to the
  no-clipping rule, and it is stated in `check_char.py`: that gate rasterises boxes under a diffuse
  model, so its guarantee is now "no equipment albedo clips under diffuse light", not "nothing on an
  equipped figure ever reaches white". The albedo bound itself is unchanged and still gated.
- **Tint on the CPU, not in the shader.** The albedo ceiling has to be enforceable by a test.
- **`WORN_SET_SPREAD_MIN` 0.36 -> 0.29.** Moving the light materials off the large up-facing
  surfaces is a real reduction in the figure's lit AREA, so `p90 - p10` necessarily falls with it.
  The art was first pushed back up as far as the direction honestly allows (a steel brow bar and a
  wider crown rim took it 0.2822 -> 0.3261), and 0.36 was then shown to be unreachable: restoring the
  pale steel pauldron caps the captures complained about still measures only 0.3520. The constant's
  own comment block mandates exactly this ("THE NUMBER FOLLOWS THE ART, NOT THE REVERSE"), with a
  precedent in loop 2, and the measurement and the failed experiment are recorded beside it. The gate
  still discriminates: the dressed figure measures 0.3261 against the undressed figure's 0.2780.
  **This is the one gate constant this wave moved, and it is the one thing in it most worth a second
  opinion.**

## Verification

- `dotnet build dream\dream.csproj`: 0 errors (17 pre-existing warnings).
- Full suite, twice, serialised on an otherwise idle machine. First run: 2 failures, both real and
  both this wave's — `ShadowFrameContractTests` and `FireShadowListContractTests` each pin that a
  visibility term is multiplied in at exactly one place, and the equipment specular is a second,
  deliberate place (armour must not glint in shadow, and a torch highlight must die in a fire's
  shadow as its diffuse pool does). Both tests were extended to cover the new call and to pin its
  multiply by text; neither was weakened. Second run after the fix: **Failed 1 / Passed 9822 /
  Skipped 47 / Total 9870**, the one failure being
  `Wave8CommandStallCorrectionLoopbackTests.SendStall_OneSecond_...`, a network-timing test with no
  connection to rendering, which passes **18/18** in three isolated runs. Treated as a load flake,
  not this wave's.
- `python tools/ui-assets/enginelook.py`: PASS (20 contract lines). `check_char.py`: 0 problems.
  `check_sources.py`: 2 failures, **both pre-existing on master** (unclassified `read_text` of the
  shader files at `enginelook.py:525` and `:640`); verified by running it on master.
- Shader compilation and linking: verified by the engine itself. Every capture below is a real
  `dream.exe` run that drew frames with no GL error and no `[render-fault]` line.
- Visual, through `skills/capturing-dream-screenshots` with `DREAM_LOOPBACK_REMOTE_PLAYERS=1` (the
  gallery cycles the whole catalog every three seconds) at a 1600x1800 window, where the figure is
  about 90 x 180 px instead of the shipped 28 x 101. In `artifacts/captures/`:
  `equip-before` and `equip-before-night` (the old boxes), `equip-after-1` (the washed-out first
  pass), `equip-nospec` (the A/B that isolated the specular as the cause), `equip-after-2`,
  `equip-after3-noon`, `equip-after3-midnight`, `equip-grain`, `equip-sweep`, `equip-plate`,
  `equip-final-noon`, `equip-final-midnight`, and `equip-motion` (walking and attacking, which is
  where the rig alignment was checked: helm, armour, cloak and weapons stay on their nodes through
  the walk cycle and the attack swing, and the gear changes live every three seconds).

## Open items

- `check_sources.py` has been red on master since before this wave; two `read_text` calls in
  `enginelook.py` need `READ_ALLOW` entries. Not fixed here because the file belongs to another area.
- Each leg still carries two bright rims in some combinations, because the leg piece and the foot
  piece are allowed one each and they meet at the ankle. A third art loop would merge them.
- The mesh caches are per renderer (local and remote, held and worn), so a shape used by both figures
  is uploaded more than once. Each mesh is at most a few hundred triangles; a shared cache would cost
  more in lifetime bookkeeping than it saves.
- The reviewer's remaining nits are recorded in the review and are all cosmetic: a few assertions in
  the catalog tests that cannot fail, and the albedo ceiling being quoted as 0.40, 0.42, 0.44 and
  0.46 in four different comments. Worth one tidying pass.
