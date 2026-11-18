# Work log: skill effect visuals and the effects skill

- Purpose: record the client FX pipeline for skill casts and the repo-local skill that documents it.
- Status: done, on branch `ws/fx-skill` from `integrate/skill-fx` f7205d72.
- Scope of this entry: documentation only. The effects themselves landed in the three workstreams merged into
  `integrate/skill-fx`.
- Plan: `docs/ledger/plans/2026-11-18-skill-effect-visuals.md`.

## What landed

- The particle atlas gained four cells: 23 `arc` (coverage), 24-25 `bolt.0/1` (zero alpha), 26 `ring` (coverage).
  Cells 27-31 stay reserved. `dream/ui/particles/particles.png` is ~38.8 KB and `MAX_PNG_BYTES` in
  `tools/ui-assets/tests/check_particles.py` was raised to 40 KB; dimensions, grid and VRAM cost are unchanged.
- `FxSimulation.SpawnSkillCast` with `EmitArc` / `EmitBolt` / `EmitMend`, bounded by `MaxCastParticles` (19) and
  `CastPoolReserve` (128) so a cast cannot starve ambient dust. New `FxParticleKind` values `Arc`, `Bolt`, `Ring`,
  mirrored by `FxFrame` in `dream/Rendering/Effects/FxInstance.cs`.
- `SkillCastEvent` on the `SceneEventHub`, published by `ClientSkillCastPublisher` from
  `PlayScene.Replication.cs` and forwarded to the simulation by `FxEventBridge`. FX is not called from gameplay code.
- `skills/adding-dream-effects/`: how to add an effect end to end - claiming atlas cells in the generator, the gate
  and the C# mirror; the alpha classes; the brightness and clipping rule; zero-allocation and pool-reserve
  constraints; ground-aligned quads and the underlay pass; the event seam; and the verification commands.

## Findings worth keeping

- `FxInstance.Frame` is a variant selector chosen once at spawn, not a flipbook. Nothing advances it over a
  particle's life, so an effect is a burst of single-frame sprites, never an animation.
- Particle RGB is premultiplied emission and stays meaningful where A = 0. The world-albedo rule (channels <= 0.40
  because render colours are multiplied up to ~2.17x) does **not** apply to particles; the particle rule is
  authored RGB peak <= 245 plus `tint * brightness <= 0.9` at run time, asserted at night factors 0, 0.5 and 1.
- A cast is visible only to the caster. Remote visibility needs a snapshot `CastEvents` list and a protocol bump;
  because the client publishes an event instead of calling FX directly, that pass is purely additive.
- `check_sources.py` reports two pre-existing failures at `enginelook.py:525` and `:640`. Any third is new.
