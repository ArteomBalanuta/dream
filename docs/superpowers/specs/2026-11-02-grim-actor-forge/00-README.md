# Grim actor forge — spec pack

A specification for **a tool that generates `dream`'s actor models, animations and assets**: original,
procedural, deterministic, and deliberately grimmer and darker than the reference art that prompted it.

It builds on what already exists. `tools/ui-assets/forge.py` is a working relief-and-material renderer and
`tools/ui-assets/isoforge.py` already renders true 3D meshes to isometric sprites with a torch-lit rig and
numeric art gates. **This is not a new pipeline.** It adds the three things actors need and props do not:
skeletons, time, and a darker rig.

## Read [06](06-output-target.md) first

There is one unresolved fork, and it decides the shape of the tool. VERIFIED: the game's player and wolf are
**third-party CC0 glTF models** (`kenney-blocky-character-a.glb`, `wolf.glb`) loaded as rigged 3D with named
animation clips, and nothing in C# consumes sprite art for actors. Files 02–04 are written for the sprite
target; the engine consumes the glTF target today.

[06](06-output-target.md) lays out both with evidence and recommends **glTF first, with sprites as a second
back end**. The skeleton, pose, clip and skinning model in [03](03-animation-model.md) is shared either way,
which is why specifying it already was safe.

## Files

| File | Covers |
|---|---|
| [01-art-direction.md](01-art-direction.md) | The grim look as numbers: `GRIM_RIG`, the `grim.*` palette, value and saturation ceilings, what is rejected from the reference |
| [02-tool-architecture.md](02-tool-architecture.md) | Reuse versus new, module layout, the changes to `isoforge.py`, CLI, environment, determinism contract |
| [03-animation-model.md](03-animation-model.md) | Skeleton, skinning, clips, facings, the baseline clip set, measured cost, output files and manifest |
| [04-gates-and-verification.md](04-gates-and-verification.md) | `check_char.py`: the measured prop baseline, the actor gate table, animation gates, determinism |
| [05-open-questions.md](05-open-questions.md) | What you decide, what needs evidence, risks, out of scope |
| [06-output-target.md](06-output-target.md) | **The blocking fork: sprites, glTF, or both** |

The workstream breakdown and merge order live in the ledger plan, not here.

## Claim tagging

**VERIFIED** read from this repo's source on 2026-11-02. **MEASURED** from a render on this machine.
**DERIVED** from the reference analysis plus a stated adjustment. **INFERRED** design judgment — treat as a
suggestion, not a fact.

## The brief in three lines

1. Blackened iron and bone lit by one guttering torch in a cold, wet dark.
2. Nothing is bright. A few things are less dark. Focus comes from value contrast, never from colour.
3. The tool is deterministic, gated by numbers, and runs on `numpy` and `pillow` alone.

## Hard rules

These override anything that seems to differ in a single file.

- **Original art only.** Never trace, sample, or read a reference image from a generator. No name from another
  game appears in a sprite, a file or a token.
- **No new dependency.** `numpy==2.5.4` and `pillow==13.0.0`, pinned. No Blender, no scipy, no GPU.
- **Byte-deterministic output.** Same inputs, same bytes, independent of worker count.
- **Material noise is evaluated in bind-pose (rest) space.** Posed-space noise makes grime swim between frames.
  Under the sprite target this is the one constraint that, if broken, invalidates the whole tool.
- **Generated output never goes to `dream/ui/`.** Matching `isoforge`'s rule.
- **The prop set stays byte-identical.** Any change to `isoforge.py` is proved by a full `check_iso.py` run
  before it is committed. If it fails, stop and report — never loosen the gate.
- **A gate is a claim about the art direction.** If a gate and the art disagree, decide which is wrong in
  writing before changing the number.
- **Build one actor before building a roster.** The art direction's darkness is unvalidated until a review
  sheet exists.

## First actors

Corrected against what the game actually spawns: **the player** (humanoid) and **the wolf** (quadruped, ~9.6 u
tall, clips `Idle`/`Walk`/`Attack` to match `WolfAssetDefinition`). An earlier draft assumed humanoid undead;
that was speculation, and the roadmap shows the wolf is the only enemy today.

## Status

Specification only. No tool code written. The art-direction numbers are starting points to be retuned against
the gates on the first render. [06](06-output-target.md) question 0 is blocking;
[05](05-open-questions.md) lists the rest.
