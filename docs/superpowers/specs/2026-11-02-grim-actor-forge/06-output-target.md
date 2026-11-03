# 06: Which output does the forge produce?

**This is the one question that changes the shape of the whole tool, and it needs your answer before any code
is written.** Everything else in this pack is written for Target A below; if you pick B, files 02, 03 and 04
need real revision while 01 survives intact.

## What the game actually ships today

VERIFIED 2026-11-02, by reading the source and the asset tree:

| Fact | Evidence |
|---|---|
| The player is a **third-party CC0 glTF model** | `dream/animations/players/kenney-blocky-character-a.glb`, beside `LICENSE-Kenney-Blocky-Characters.txt` |
| The only enemy is a **third-party CC0 glTF wolf** | `dream/animations/enemies/wolf.glb` |
| The engine loads it as a **rigged 3D model with named animation clips** | `WolfAssetDefinition`: `IdleClip = "AnimalArmature\|Idle"`, `MoveClip = "…\|Walk"`, `AttackClip = "…\|Attack"`, plus `Normalization(3.191f, 0.60f)` and `AuthoredForwardAxis` |
| The whole actor and prop layer is a **curated vault of CC0 packs** | `dream/animations/library/README.md`: Kenney, Quaternius, Poly Haven, OpenGameArt — "the curated source vault for future environment, creature, and player-content work" |
| The wolf is a **quadruped, 0.60 m tall, 0.30 m radius** | `WolfDefinition.CollisionHeight`, `CollisionRadius` — roughly 9.6 u tall, not a humanoid |
| **Nothing in C# consumes sprite art for actors** | No consumer of `iso_manifest.json` anywhere in `dream/**/*.cs` |

So when you say "create our own models/assets/animations", the thing being replaced is a set of CC0 glTF packs
that the existing 3D renderer loads by clip name. That is a different target from the one the rest of this pack
assumes, and I should not have assumed it silently.

## The fork

### Target A — isometric sprite sheets (what files 02–04 describe)

`charforge` skins meshes, renders each frame through `isoforge`'s chain, and writes PNG frames plus a manifest.

| | |
|---|---|
| **Reuses** | The entire `forge`/`isoforge` chain: GGX lighting, baked AO, cast shadow, rust and grime passes, ACES finish, the grade, the downsample. The grim look comes almost free |
| **Look control** | Total. Light is **baked into the pixels**, which is exactly how the reference achieves its mood |
| **Engine work** | None now; a new `ActorAtlasTable` loader later, and it only becomes visible after `integrate/isometric` lands |
| **Cost** | MEASURED ~4.5 s per frame; ~1.8 min per actor on 4 workers |
| **Risk** | The output sits unused in `tools/ui-assets/char/` until two other things land. It is art you cannot see in the game yet |

### Target B — original glTF models with skeletal clips

`charforge` builds the same meshes and skeletons, then exports `.glb` with a skin and animation clips, named to
match what `WolfAssetDefinition` already expects. The files drop in where the CC0 packs are now.

| | |
|---|---|
| **Reuses** | `isoforge`'s `Mesh` builders (`tube`, `lathe`, `box`, `convex`, `card`) and the skeleton/clip model from [03](03-animation-model.md). **Not** the render chain |
| **Look control** | Much weaker. The engine lights it at runtime, so the grim mood has to come from the engine's own rig, materials and grade — not from `forge.finish` |
| **Engine work** | Little or none to *load* it: the loader, clip names and normalization already exist |
| **Cost** | Cheap per asset — no rendering, just geometry and a texture bake |
| **New work** | A glTF writer (mesh, skin, animation samplers, PBR material, texture atlas). `numpy` + `pillow` can do it, but it is a real component and none of it exists |
| **Risk** | The thing that makes our 2D art good — baked, art-directed light — is precisely what a glTF cannot carry |

### Target C — both

Target B for geometry, rigs and clips that the engine renders today, and Target A reusing the same
`ActorSpec` to render review sheets and, later, isometric sprites. The skeleton, clips and material definitions
are shared; only the back end differs.

This is the most work and the most future-proof. It is also the only option that gives you something visible in
the game *and* keeps the baked-light look available.

## My recommendation

**Target B first, structured so A is a second back end** — in other words C, built in that order.

The reasoning is not about effort, it is about where the look lives. The reference analysis in this session
concluded that the mood comes from *localised warm pools, cold ambient, filmic roll-off and split-tone grading*
— and your engine already has an LDR/FX wave for exactly that (`integrate/fx-ldr`, per the project history).
If the engine owns the grade, then baking a second, different grade into sprite pixels fights it. Original
geometry with grim materials, lit by a grim engine rig, is the coherent version.

Target A remains worth building, but as the second back end once the isometric view ships, not first.

## What changes per target

| File | Target A | Target B |
|---|---|---|
| [01 art direction](01-art-direction.md) | As written | **Survives**, but `GRIM_RIG` becomes a spec for the *engine's* rig rather than a Python constant, and `sat_cap` moves into material authoring |
| [02 architecture](02-tool-architecture.md) | As written | The three `isoforge` hooks are unnecessary; `Mesh.rest` is unnecessary; a glTF writer replaces `render_frame` |
| [03 animation model](03-animation-model.md) | As written | Types, skeleton, skinning and clips **survive unchanged**. Canvas, facings, cost, sheet and manifest are all replaced by glTF skin and animation samplers |
| [04 gates](04-gates-and-verification.md) | As written | Pixel gates do not apply. New gates: valid glTF, clip names match `WolfAssetDefinition`, bone count, bounds match `WolfDefinition`'s collision dimensions, determinism of the bytes |
| [05 open questions](05-open-questions.md) | As written | Questions 2, 5, 6 and 7 become moot; new ones appear about texture resolution and material export |

**The shared core is [03](03-animation-model.md)'s skeleton, pose, clip and skinning model.** Whatever you
choose, that part is built the same way, which is why it is safe to have specified it already.

## First actors, either way

Corrected from an earlier draft, which assumed humanoid undead. The roster should match what the game spawns:

1. **The player** — humanoid, replacing `kenney-blocky-character-a.glb`.
2. **The wolf** — quadruped, ~9.6 u tall, replacing `wolf.glb`, with clips named `Idle`, `Walk`, `Attack` to
   match the existing constants.

A quadruped needs a different skeleton from a biped but the same machinery, so it is a good second actor: it
proves the rig model generalises before a roster is authored against it.
