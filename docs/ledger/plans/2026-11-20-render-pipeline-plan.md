# Plan: the render pipeline wave

- Branch: `feat/render-pipeline`, from master `79ed90a3`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — the architectural
  spine built and committed first, then three implementers on disjoint files.

## What the inspection found

The whole renderer had **one framebuffer object: the shadow map**. Everything else — terrain,
props, actors, sky, boundary mist, effects, HUD — drew straight to the default framebuffer, in LDR,
in one pass.

That single fact explains most of the ceiling the project has been working against:

- **Every channel was hard-clipped at 1.0.** The art is held to an albedo ceiling of 0.42 because
  0.42 × the peak gain of ~2.2 beside a brazier is 0.924: the *diffuse* term alone nearly filled the
  budget, so a specular lobe, an equipment rim or an emissive on top of it landed on flat white.
  Three separate waves have tuned around that clip.
- **No bloom was possible.** `fxFragment.glsl` writes its atlas texel straight out with a comment
  saying it is deliberately not exposed, "the atlas holds light that is meant to read as bright" —
  which is as bright as a sprite can be when 1.0 is the ceiling.
- **No tonemap, and correctly so.** The shaders write display-referred values; the catalogue was
  authored by eye against the monitor. A conventional filmic curve would move every pixel in the
  game and invalidate the golden baselines, the character palette gates and years of tuning.

What was NOT a problem, and was left alone: the sun shadow path is careful work — normal-offset plus
slope-scaled bias, 3×3 taps of hardware 2×2 PCF, a compile-time switch that proves the disabled
identity. Acne and peter-panning are already solved.

## The spine (committed first, `a4ad2a66`)

An RGBA16F scene target and a three-program post chain, with a curve chosen so that it can be added
to a calibrated renderer without invalidating the calibration:

**The shoulder is the identity below the knee (0.80).** Above it, `knee + head·t/(t+head)`, which is
C1 continuous (slope exactly 1 at the knee, so no seam), monotonic, and asymptotic to 1.0 so nothing
ever clips. It rolls off the max channel and scales the colour, rather than per channel, because
this art direction is built on coloured light and a per-channel curve slides a highlight to white.

**The bloom's source is the overflow and nothing else.** A pixel below the knee contributes exactly
zero, so a dark frame produces a black bloom. The brief's "without washing out dark shadows" is a
property of the threshold, not a tuning choice.

Two deliberate behaviour changes: MSAA now resolves in float (samples are averaged unclamped, so an
edge next to something overbright is brighter and more correct), and there is still no gamma step.

## Tracks

| Branch | Owns | Job |
| --- | --- | --- |
| `ws/light-atmos` | `lighting.glsl`, `pointLights.glsl`, `fireShadows.glsl`, `clouds.glsl`, `Rendering/Lighting`, `Rendering/Shadows` | point-light falloff with the new headroom, height fog for gloom depth, shadow filtering where it is actually wrong |
| `ws/surface-emissive` | `cubeFragment.glsl`, `enemyFragment.glsl`, `fxFragment.glsl`, `warMistFragment.glsl`, `skyFragment.glsl`, `Rendering/Effects`, `EquipmentSurface.cs` | emissive and sparks that reach the bloom range, one material response across the two big world shaders |
| `ws/render-arch` | `PlayScene.Render.cs`, `GpuFrameTimer.cs`, `RenderCounters.cs`, `Render.cs`, `StaticWorldChunkRenderer.cs` | time the post chain, remove redundant state, measure batching before touching it |

Disjoint by file. The frozen contract: every existing `dream*` GLSL function keeps its name and
signature, so a track may change a body but never break a caller it does not own.

## The net

The golden baselines were **already failing five of six scenes on master** before a line of this
wave was written — three equipment waves had landed without a re-record. They were re-recorded over
10 runs first (`96fc7b09`) so this wave has a real reference, and will be re-recorded again at the
end, deliberately, once the visible changes are settled.

## Constraints carried through every brief

- OpenGL 3.3 core / GLSL 330 only; every FBO checked complete; every failure path non-fatal and
  falling back to the pre-existing behaviour.
- `half` is a reserved word in GLSL 330.
- Normalised Blinn-Phong, `(shininess + 8) / (8π)`; without it a rough material becomes a second
  diffuse light.
- The figure is 28×101 px at the shipped zoom. Frequency, not amplitude.
- `enginelook.py` pins 20 shader lines character-for-character.
