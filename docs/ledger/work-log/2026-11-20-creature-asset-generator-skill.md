# Work log: creature-asset-generator skill

- Purpose: add a repo-local skill that generates rigged, textured creature and character models (.glb) procedurally in Python.
- Status: committed to `master`. Skill is source tooling only; nothing in the game consumes its output yet.
- Scope: `skills/creature-asset-generator/` (SKILL.md, scripts, two designs, references) and two `.gitignore` lines.

## What it is

A sculpt-to-game-asset pipeline with no Blender: signed-distance primitives (with groups, shells,
trim planes and noise holes for armour and cloth) are polygonized with marching cubes, decimated to
a triangle budget, unwrapped with xatlas, and baked to 2048² albedo / normal / ORM maps straight from
the sculpt. Skin weights come from the primitive that owns each surface; animations are keyframed
per design. A four-view turnaround viewer (three.js) is used to compare against reference images.

Reference designs, both checked visually in that viewer:

- `designs/hare.py`: Grey Hare, 13.4k triangles, fur strands, eyes, whiskers, Idle + Alert.
- `designs/skeleton_warrior.py`: ~360 primitives, 26k triangles, bone / rust / steel / cloth /
  leather, Idle + Rattle.

Not verified: the Khronos glTF validator, and import into any engine.

## Fit with this engine

The output has the same mismatch recorded in `2026-11-19-royal-sorcerer-set.md`: a skinned mesh with
its own armature and PBR albedo meant for a tonemapped renderer. Dream draws equipment as unskinned
parts on a rigid hierarchy with no tonemap, so bright albedo clips. Use the skill for source and
reference art until an import path (rigid split per bone, albedo range check) exists.

## Setup

Python deps live in a git-ignored `skills/creature-asset-generator/.venv`, created per machine
(`numpy scipy pillow scikit-image fast-simplification xatlas`). Build outputs go to the ignored
`out/creatures/`.
