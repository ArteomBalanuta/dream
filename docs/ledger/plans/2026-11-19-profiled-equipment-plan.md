# Plan: profiled equipment shells (pass 4)

- Branch: `feat/profiled-equipment`, from master `436feb7a`.
- Driver skill: `lowpoly-armor-generator` (mkills), for its one central technique.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — the shape library
  frozen and committed first, then three implementers on disjoint files.

## What was actually left

The brief was "equipment is blocky and Minecraft-like". Pass 3 was taken to mean *remove the plain
boxes*, and it did: an audit at the head of this pass counted **50 models — 27 worn, 23 held — with
301 worn parts and 209 held parts, and not one plain `EquipmentShape.Box` anywhere**. The obvious reading of the brief
was already satisfied, and something else was producing the slab look.

It is the shapes themselves. `Plate` is the most used shell in the armour catalogue and it is a
**cylinder**: one radius, one sweep, three rings at 0.96 / 1.00 / 0.96. A constant section reads as
an extruded shape however curved its ends are — which is exactly what "blocky" means to the eye
even when nothing in the model is a cube. The same is true of `Panel`, whose whole surface variation
is one gentle bulge; across a part twenty pixels wide that is one flat value, and it is why the
gossamer wings never stopped reading flat.

## The technique taken from the skill

The `lowpoly-armor-generator` skill builds every rounded form with
`loft(centers, radii, angles)` — a **ring table**, where the centre, the two ellipse radii and the
arc extent are all listed per ring. That one function is what gives its sorcerer set a swelling
pauldron and a cinched waist out of the same primitive.

The skill's own output could not be used here (see the royal sorcerer plan for the four measured
reasons), but the technique ports exactly. `AppendArcShell` is `loft` in this project's unit frame:
an open arc about +Z with per-ring radius, sweep and centre, fitted to the unit box afterwards so
every shape remains a drop-in for a `Plate`.

## What landed in the frozen contract (commit `04becf12`)

| Shape | Ring table | The defect it answers |
| --- | --- | --- |
| `Pauldron` | 5 rings, sweep 250°→190°, radius swelling then tucking | shoulder caps that read as slabs |
| `Cuirass` | 6 rings, hourglass, elliptical section (rz ≈ 0.75 rx) | torsos that read as barrels |
| `Greave` | 5 rings, taper 0.50→0.29, centre walking toward +Z | limb shells that read as pipes |
| `Lame` | 8 rings, three steps with hard rims between | layered plate faked with stacked thin parts |
| `Membrane` | ribbed sheet, 4 ribs, normals split at each ridge | `Panel`'s single bulge reading as one value |

Triangle budgets: 260 / 300 / 260 / 360 / 330. All five pass the existing shape gates unchanged —
inside the unit box, filling it on at least one axis, unit normals, UVs in range, no degenerate
triangle, and every triangle wound and normalled outward.

## Workstreams

| Branch | Owns | Job |
| --- | --- | --- |
| `ws/armour-profiled` | `ArmourMeshCatalog.cs` | adopt the shells across all 27 worn models |
| `ws/weapons-profiled` | `WeaponMeshCatalog.cs` | the three shields first, then hilts and hafts |
| `ws/equip-surface` | the three GLSL files, `EquipmentSurface.cs`, `EquipmentTintCatalog.cs` | pattern frequencies, a per-material edge rim, material separation |

Disjoint by file. The shape library is frozen for the duration: no workstream may add a shape.

## Constraints carried forward from earlier passes

- No tonemap in the world pass; gain reaches ~2.2, so albedo stays at or under 0.42 per channel and
  any new lighting term is measured against the clip ceiling before it is chosen.
- Normalised Blinn-Phong, `(shininess + 8) / (8π)`. Without it a rough material becomes a second
  diffuse light and washes the figure to cream.
- Frequency, not amplitude. The figure is 28×101 px; a feature finer than about 1/12 of a face is
  sub-pixel and resolves to flat grey. This has been got wrong three times.
- A shell's axis is +Y and its opening faces −Z, so to CAP something, `pitch: -90f`. And `yaw: 180`
  flips the sense of `roll`.
