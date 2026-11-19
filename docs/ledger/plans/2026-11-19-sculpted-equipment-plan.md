# Roadmap — sculpted equipment: shapes, material response and tint, wave "sculpted-equipment"

## Goal

Worn armour, weapons and equipment stop reading as stacked Minecraft blocks and start reading as
sculpted gothic dark-fantasy gear: curved plates, bevelled edges, ornate blades, guards and helms,
with a real metallic/roughness response, surface grain and wear, and per-item colour variation.

Reference direction (for the eye only — nothing is traced, sampled or copied): Mu Online and
Diablo II: Resurrected.

## The two facts that shape every decision

**Scale.** The whole figure is 28 x 101 px at the shipped isometric zoom (measured again this wave at
a 1600x1800 window: 88 x 178 px, so the shipped 1440x1024 window really is ~28 x 101). What survives
is the **silhouette**, the **mass**, the **value** — and, newly, the **run of light across a surface**.
That last one is the opening: a curved pauldron gives a gradient where a box gives one flat value, and
a gradient reads at any size. The 2026-11-05 wave concluded "a loft-smooth attachment buys nothing
visible" and authored plain boxes; this wave deliberately reverses that call, on the grounds that
curvature is a *shading* change, not an ornament change.

**No tonemap.** The world pass computes `(ambient*scale + direct*0.75) * albedo * exposure` and clamps.
The gain reaches ~2.2 beside a brazier, so an albedo channel above ~0.42 clips to a flat patch. Every
colour in this wave, tints included, stays under that ceiling, and `EquipmentTint.Apply` enforces it in
code rather than by convention. Specular is the one deliberate exception: a highlight is allowed to
reach white, because that is what a highlight is.

## Architecture

The equipment draw path already has the right shape: `WeaponMeshCatalog` / `ArmourMeshCatalog` hold a
handful of parts per item, and `HeldWeaponRenderer` / `WornArmourRenderer` draw each part as one
batched unit-cube draw off a shared mesh, riding the posed skeleton node. Nothing about attachment,
animation, physics or the dev overlay needs to change. What changes is **what mesh a part draws** and
**how the shader answers light**.

1. **`EquipmentShape` + `EquipmentShapeLibrary`** — twelve sculpted unit meshes (bevelled box, shaft,
   taper, blade, plate shell, dome, spike, ring, crescent, gem, cloth panel, and the legacy box),
   each inscribed in the old unit cube so a part's authored Center/Size/Pitch/Roll/Yaw still place it
   exactly where the box was. `Box` is unchanged, so untouched content is bit-identical.
2. **`EquipmentSurface` + `EquipmentTint`** — metallic, roughness, grain and accent-mix per material,
   and a primary/accent colour pair per item. The tint is applied on the CPU to the part's
   `baseColorFactor`, so the albedo ceiling stays testable.
3. **`enemyFragment.glsl`** — a block gated on a `uEquipSurface` uniform that is zero for every other
   actor, enemy, NPC and prop draw: Blinn-Phong specular against sun, moon and the point lights,
   fresnel edge sheen, and procedural surface grain. At zero the shader is line-for-line what it was,
   and the lines `enginelook.py` pins are untouched.
4. **Re-authored catalogs** — every model re-sculpted onto the new shapes, with gothic silhouettes and
   more ornament where ornament reads.

## Workstreams (frozen contracts at `equip/contracts`, e69d6a86)

| id | owns | builds |
| --- | --- | --- |
| WS-A | `EquipmentShapeLibrary.cs` + its test | the twelve sculpted meshes |
| WS-B | the two renderers, `EquipmentSurface.cs`, `EquipmentMeshCache.cs`, `EquipmentTintCatalog.cs`, `Shader.cs`, `enemyFragment.glsl`, `pointLights.glsl` + tests | the draw path, the material response, the tint |
| WS-C | `WeaponMeshCatalog.cs`, `ArmourMeshCatalog.cs`, `HeldItemScale.cs`, `tools/ui-assets/tests/check_char.py` + tests | the re-authored art and the Python gate |

No two workstreams own a file. Merge order: A, then B, then C.

## Hard constraints

- Camera, base character model, BulletSharp physics and the ImGui dev overlay are untouched and
  regression-free.
- Equipment keeps riding the posed bones: helms, armour and weapons follow animations and update live
  on a gear change. `AppendCorners` stays correct because every sculpted shape is inscribed in the
  part's box, so the capture `actorbox` stays a conservative bound.
- Meshes: outward winding, unit normals (split where a facet is wanted, smooth where a curve is),
  UVs in [0,1], inscribed in the unit frame. Pinned by `EquipmentShapeLibraryTests`.
- GLSL 330 compiles and links clean, with the equipment uniform at zero a provable no-op.
- `tools/ui-assets/enginelook.py` and `tools/ui-assets/tests/check_char.py` stay green.

## Verification

- `dotnet build dream\dream.csproj`, filtered `dotnet test` per workstream, then a combined run on the
  integration branch.
- `python tools/ui-assets/enginelook.py`, `check_char.py`, `check_sources.py`.
- In-engine captures through `skills/capturing-dream-screenshots` with
  `DREAM_LOOPBACK_REMOTE_PLAYERS=1` (the gallery cycles the whole catalog) at a 1600x1800 window, noon
  and midnight, before and after, judged on silhouette, alignment, shading and tint.
