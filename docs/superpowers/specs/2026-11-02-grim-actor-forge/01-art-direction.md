# 01: Grim art direction

The look target for every actor the forge makes. It is a **deliberate darkening** of the reference analysis:
same structural techniques, roughly two thirds the value, two thirds the saturation, and no candy accents.

All art is original and procedural. Never trace, sample or read a reference image from a generator.

## Claim tagging

**VERIFIED** read from this repo's source. **MEASURED** from a render on this machine. **DERIVED** from the
reference analysis plus a stated adjustment. **INFERRED** design judgment.

## The one-paragraph brief

Blackened iron and bone lit by a single guttering torch in a cold, wet dark. Material reads as *weight and
age*, never as polish. The eye finds an actor because it is the only thing catching the key light, not
because it is colourful. Nothing in the frame is bright; a few things are *less dark*.

## Decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Projection | Inherited from `isoforge`: pitch 50°, yaw 45°, 4 px per u final, rendered 4x over. No change |
| 2 | Value ceiling | `p99_v` in **[0.28, 0.50]**, `peak_v` ≤ **0.62**. Props MEASURE p99 V 0.40–0.66 and peak V 0.49–0.69, so this is a real drop with a floor that stops mud |
| 3 | Saturation ceiling | `sat_p95` ≤ **0.26**, `sat_p99` ≤ **0.34**. Props MEASURE sat p95 0.31–0.62. Achieved through `IsoMat.sat_cap` **0.22–0.26** on cloth, leather and iron, **0.28** on skin |
| 4 | Accent budget | At most **3 %** of an actor's opaque pixels may exceed S 0.35, and the one accent material caps at `sat_cap` **0.40** |
| 5 | Forbidden hues | No pink, magenta, acid green, cyan or pure white anywhere in actor art. Cold is blue-grey, not cyan. Warm is ember-brown, not orange. Enforced numerically in [04](04-gates-and-verification.md) |
| 6 | Black floor | `CHAR_CRUSH` = **0.005**, slightly *below* `isoforge`'s 0.008. See the note below — this is a far smaller lever than it looks |
| 7 | Rim light | Keep the cold rim, drop it to 0.9 from 1.2. The ghost-cyan outline of the reference is **rejected**; our undead get a dull bone-grey edge |
| 8 | Emission | Only for: eye sockets, rune strokes, and held light sources. **No `emit` on ordinary actor materials.** Bloom drops to 0.6 so no actor approaches the potion's MEASURED peak V 0.958 |
| 9 | Outlines | No shader outline pass in the art. The silhouette reads from the hard 1 px alpha edge `isoforge` already produces |
| 10 | Grime default | `wear=.35 grime=.65 rust≤.12 dust=.20` for actors, against the prop default `.3/.5/.2/.1`. Grime up, rust **down** — armour rust must not reach the barrel's orange |
| 11 | Palette source | Every actor colour resolves through `tokens.json` under the `grim.*` namespace. No literal hex in a generator |
| 12 | Resolution contract | Actor sprites are **not** pixel art. The reference's resolution-mismatch trick is replaced by smooth-rendered actors with hard alpha edges and a coarse form budget |

### Note on decision 6: `CRUSH` is a near-black cutoff, not a mood dial

VERIFIED from `isoforge._grade`: `y2 = clip((y − CRUSH)/(1 − CRUSH), 0, 1) ** 1.08`, where `y` is sRGB luma on
0..1. MEASURED: `CRUSH` 0.005, 0.008 and 0.014 crush everything at or under **1.3, 2.0 and 3.6 of 255**.

This matters because an earlier draft of this spec proposed *raising* `CRUSH` to 0.014 to deepen the shadows,
and the architect's plan proposed *lowering* it to 0.005 to protect the cold-band gate — which measures pixels
in luma **12..60** of 255. Both were wrong about the mechanism: at any of these values `CRUSH` never reaches
the gate's window, and it cannot deepen a shadow that is already above luma 4.

What actually controls shadow depth and the cold-band population is **exposure, fill intensity and albedo**.
`CHAR_CRUSH` 0.005 is kept only as cheap insurance for the deepest shadow detail, and **no gate depends on it**.

## Lighting rig: `GRIM_RIG`

A darker sibling of `ISO_RIG`, same directions so cast shadows stay consistent across a scene. Field names are
`forge.Light(dir, color, intensity, shadow)` and `forge.Rig(lights, panels, ambient)` (VERIFIED against source).

| Light | `ISO_RIG` today | `GRIM_RIG` | Why |
|---|---|---|---|
| Torch key | dir `KEY_DIR` = (−0.6, −0.6, 0.53), color (1.00, 0.76, 0.52), intensity **18.0** | color **(1.00, 0.72, 0.46)**, intensity **11.0** | 0.61x the energy. The colour goes *more* amber, not less: `warm_lit` is a mean R−B and must hold its 3.0 floor at the lower intensity, so **hue carries that gate, not brightness** |
| Cold fill | (0.22, 0.46, 1.00), **6.0** | color **unchanged**, intensity **4.2** | Fill-to-key rises 0.33 → 0.38, so the shadow side stays blue and `cold_band` stays negative. The colour is left alone precisely because the cold gate depends on it |
| Cold rim from below | (0.30, 0.52, 1.00), **1.2** | **0.9** | Decision 7. Less silhouette lift: actors read as mass, not outline |
| Reflection panel | (0.88, 0.94, 1.00) @ 0.16 | **@ 0.10** | Panels are what read as chrome. Dimming drops `chrome` further below its ceiling |
| Ambient | (0.0050, 0.0130, 0.0460) | **(0.0035, 0.0095, 0.0340)** | Same hue at 0.73x. Still far under 15 % of the new key |
| `exposure` | 1.0–1.2 on props | **0.90** | The last global dial. **Tune this before touching the rig** |
| `dome` | 20 directions | **10** | MEASURED: cutting the AO dome saves ~38 % of render time for a slight art effect. A cost decision, not a look decision |

An earlier draft of this spec desaturated the fill to (0.26, 0.38, 0.58) and cooled the key to (1.00, 0.68, 0.40).
Both are **withdrawn**: they attack the two gates — `cold_band` and `warm_lit` — that keep the chiaroscuro
readable. Desaturate through `sat_cap` and albedo, which act per pixel after the grade, not through the rig.

## Palette

Linear-light materials, sRGB hex given for review only. All go in `tokens.json` as `grim.*`.

Albedo saturation is not pixel saturation: `_grade` applies `sat_cap` per pixel after lighting, so `grim.rust`
at albedo S 0.70 lands near its cap on screen. The gates in [04](04-gates-and-verification.md) measure
rendered pixels.

### Core materials

| Token | sRGB | Role | `rough` | `f0` | `sat_cap` |
|---|---|---|---|---|---|
| `grim.iron.black` | `#17181A` | Plate, blades, bands — the default actor metal | 0.62 | 0.035 | 0.22 |
| `grim.iron.pitted` | `#212022` | Older iron, more cavity rust | 0.72 | 0.030 | 0.24 |
| `grim.steel.bare` | `#3A3B3E` | Exposed edge where wear cut through | 0.44 | 0.22 | 0.22 |
| `grim.bone` | `#4A4740` | Skulls, ribs, bone fetishes. Warm-grey, never white | 0.80 | 0.028 | 0.24 |
| `grim.bone.old` | `#33302B` | Buried, stained bone | 0.88 | 0.025 | 0.26 |
| `grim.leather` | `#241C16` | Straps, boots, jerkins | 0.84 | 0.035 | 0.26 |
| `grim.cloth.dark` | `#1A1720` | Cloaks, hoods, wraps. Violet-black | 0.92 | 0.030 | 0.24 |
| `grim.cloth.ash` | `#2A2724` | Bandage, sackcloth, rags | 0.94 | 0.028 | 0.22 |
| `grim.flesh.grey` | `#3B332E` | Undead and corpse flesh | 0.86 | 0.032 | 0.28 |
| `grim.horn` | `#282018` | Horn, claw, chitin | 0.70 | 0.040 | 0.26 |
| `grim.stone.wet` | `#1E2022` | Statuary, grave slabs | 0.78 | 0.032 | 0.22 |

Skin albedo is stated explicitly because it is the one material that drifts warm by accident:
**linear (0.055, 0.040, 0.034)**, deliberately desaturated. Cloth and leather sit at linear **0.015–0.030**
with a slight cold bias. Prop iron, for comparison, is ~0.011–0.012 linear. Together these hold `median_s` near
**0.13–0.18** — above the 0.10 "neutral grey" floor, far below bramble's MEASURED 0.510.

### Accents — one per actor, 3 % budget

| Token | sRGB | Use | `sat_cap` |
|---|---|---|---|
| `grim.rust` | `#4A2A16` | Cavity and seam rust. Pooled by `Scene._rust`, capped by `CHAR_RUST_CAP` = **0.34** against the props' 0.44 | 0.34 |
| `grim.blood.old` | `#3A1416` | Dried blood in crevices. Never wet, never bright | 0.34 |
| `grim.verdigris` | `#1E2A22` | Oxidised brass on relic gear. **Violates the hue rule at hue 140 — see [05](05-open-questions.md) question 1** | 0.24 |
| `grim.ember` | `#5A2A0C` | Emissive ember, rune strokes, forge heat. Decision 8 limits where this may appear | 0.40 |
| `grim.corpselight` | `#1C2630` | Emissive eye sockets, necrotic runes | 0.30 |
| `grim.gold.dull` | `#453418` | Tarnished inlay on named gear only. **Never on a common enemy** | 0.28 |

Compare with the reference's amber-vs-violet split: we keep the *structure* — warm key, cold shadow — and throw
away both saturation extremes. Reference floor amber sits near S 0.55; our warmest lit metal lands near S 0.22.

## Form rules

Carried from `isoforge` and tightened for actors.

- **Band budget** unchanged: primary ≥ 8 u, secondary 2–6 u, tertiary < 1 u, and the 1–2 u gap stays empty.
- **Silhouette first.** An actor must be identifiable as a black shape at 4 px/u with no interior. Asymmetry is
  the cheapest identity: one pauldron, one horn, a broken limb, a dragging chain.
- **Scale.** VERIFIED: 16 u = 1 m, so a 1.8 m humanoid is ~29 u tall. At pitch 50 its vertical screen extent is
  about 0.77x that plus footprint, which is why [03](03-animation-model.md) sizes the canvas at 160x192 px.
- **Readable mass.** A detail under 1.5 u will not survive the downsample. Spend the budget on 3–4 primary
  masses: head, torso, weapon, and a single silhouette hook.
- **Wear follows convexity**, per `forge.age`. Never scatter it. If edges read as chrome, lower `wear` or the
  material's bare `f0` — do not blur.
- **Contact.** Every actor ships a separate shadow layer, same canvas, as props do.

## What we deliberately reject from the reference

| Reference technique | Verdict | Reason |
|---|---|---|
| Resolution mismatch (chunky sprites, smooth ground) | **Reject** | `isoforge` renders true 3D; faking low-res would fight the whole chain. The same separation comes from hard alpha edges plus a softer ground |
| Cyan ghost rim on undead | **Reject** | Decision 7. It is the single brightest, most saturated thing in the reference frame |
| Pink hair / magenta cloak as the focus device | **Reject** | Decision 5. Focus comes from value contrast alone |
| Acid-green loot glow | **Reject** at that saturation; keep the *idea* as a dull `grim.ember` pulse |
| Wide low-threshold bloom | **Keep, reduced** to 0.6. `forge.finish` blooms values over 1; with the key at 11.0 very little exceeds it, which is the point |
| Split-tone grade, warm highlight over cold shadow | **Keep.** It is `forge.finish`'s composition multiply, unchanged |
| Filmic / ACES roll-off | **Keep.** Essential — it is what stops the ember accents clipping |
| Localised warm ground pools | **Keep**, as an engine-side light decal. Out of this tool's scope |
| Volumetric haze softening light edges | **Keep**, engine-side. Out of scope |
| Hand-painted gothic UI chrome | **Already ours.** `forge.py` does this better than painting would |

## Acceptance, in one line

A finished actor, placed on the existing review background, must be **the darkest and least saturated thing on
that sheet** while still showing three distinguishable material reads.
