# Work log: the render pipeline wave

- Purpose: stronger gothic dark-fantasy visuals, clearer depth and material lighting, no regressions.
- Status: on `feat/render-pipeline` from master `79ed90a3`. Not pushed, not merged.
- Plan: `docs/ledger/plans/2026-11-20-render-pipeline-plan.md`.
- Method: the swarm workflow in `CLAUDE.md` with `parallel-agent-delivery` — the spine built and
  committed first, then three implementers on disjoint files, then integration and measurement here.

## The finding that shaped the whole wave

The renderer had **one framebuffer object: the shadow map**. Everything else drew straight to the
window, in LDR, so every channel was hard-clipped at 1.0. That single fact is behind most of what
the brief asked for:

- the 0.42 albedo ceiling the art is held to exists because 0.42 × the peak gain of ~2.2 is 0.924,
  so the *diffuse* term alone nearly filled the budget and anything added on top went flat white;
- no bloom was possible, which is why `fxFragment.glsl` carried a comment saying it is deliberately
  not exposed — as bright as a sprite can be when 1.0 is the ceiling;
- and a conventional tonemap was not an option, because the shaders write display-referred values
  and a filmic curve would move every pixel in the game.

What was **not** wrong, and was left alone after reading it: the sun shadow path. Normal-offset plus
slope-scaled bias, 3×3 taps of hardware 2×2 PCF, a compile-time switch that proves the disabled
identity. A texel is ~0.023 world units and the penumbra is about 2 px at the shipped zoom; acne and
peter-panning are solved. The lighting workstream measured it, concluded there was nothing to win,
and said so instead of changing it.

## What landed

**The spine — HDR target and highlight shoulder (`a4ad2a66`).** The world draws into RGBA16F and is
resolved through bright pass → separable blur → shoulder. The curve is the **identity below the knee
(0.80)**, C1 continuous (slope exactly 1 at the knee), monotonic, asymptotic to 1.0. The bloom's
source is the overflow *and nothing else*, so a pixel below the knee contributes exactly zero and a
dark frame produces a black bloom — "without washing out dark shadows" is a property of the
threshold rather than a tuning choice. Full detail in `docs/rendering-and-assets.md`.

**Lighting and atmosphere (`ws/light-atmos`).** A new shared `dreamPointWindow`, so a highlight
still dies where its pool does: `1.27·(1−s²)²/(1+8s)`, exactly 0 with zero slope at the radius,
inverse-square-like past s = 1/8. Old and new agree at s = 0.5 and the floor-disc integral matches
to 0.3% (0.25239 → 0.2527), so the scene's overall brightness is held while the core gets 17–27%
brighter — which is only useful *because* the HDR target now exists to carry it. Height fog arrives
as a new `FrameLighting` member (all zero = exactly today's fog), multiplying the density by
`1 + amount·(m − 1)` where m is the mean of `exp(−a·h)` along the ray: 1.0 level with the focus, so
flat ground is unchanged, thinner on a rise, thicker in a hollow, bounded.

**Surfaces and sparks (`ws/surface-emissive`).** `fxFragment.glsl` gains an HDR gain,
`1 + 1.5·smoothstep(0.55, 0.95, maxChannel)·(1 − alpha)`: a spark core at 1.0 becomes 2.5, which is
1.7 over the knee and blooms, while anything under 0.55 is multiplied by exactly 1.0 and smoke, dust
and ash are untouched. The `(1 − alpha)` term keeps opaque particles from glowing. A new
`surface.glsl` pulls the equipment lobe, the roughness-to-shininess map, F0 and the rim out of
`enemyFragment.glsl` with identical arithmetic, so the next shader that needs a material response
calls it instead of making a third copy.

**Architecture (`ws/render-arch`).** A `Post` GPU timer section through to the perf line, so the
chain's cost is visible rather than inferred, and a framebuffer guard: `Render` now restores
framebuffer 0 and the window viewport on **every** exit path, not just the happy one. Without it an
exception between the bind and the resolve would leave the offscreen target bound and the next scene
— `MainMenuScene` and `StartupConnectionScene` both clear and draw straight to the default
framebuffer — would render into it and show a black window with no obvious cause.

## Measured

| | before | after |
| --- | --- | --- |
| saturated pixels, world area, night frame | 0.1196% | 0.0295% |
| look gate A1 bright share (floor 0.020) | 0.017 **FAIL** | 0.033 **PASS** |
| look gate A1 near-black share (floor 0.45) | 0.776 | 0.749 |
| p99 value | 0.682 | 0.855 |
| look gate problems | 2 | 1 |

The remaining look-gate failure is `mean saturation` 0.544 against a ≤0.38 target. It is
**pre-existing** (0.549 before) and barely moved. Diagnosed: 57% of world pixels sit below value
0.05 and carry mean saturation 0.54 — our blacks are *tinted* (cold ambient, the violet boundary
mist) where the reference art's are closer to neutral. Real, worth a future pass, and deliberately
not fixed here: it would be a second global grade in the same wave as the first.

**Cost: the post chain is 0.110 ms of GPU per frame** — measured on the integrated branch with the
new timer, against a 2.33 ms GPU total in a 6.94 ms frame. About 4.7% of GPU work for the HDR
resolve, bright pass, two blurs and the shoulder.

## The golden gate was broken, it was not this wave, and the first fix was wrong

The baselines were failing five of six scenes **on master before a line of this wave was written** —
three equipment waves had landed without a re-record. They were re-recorded first (`96fc7b09`) to
give the wave a reference, and then kept failing.

The diff was a long diagonal band across the ground: a **shadow edge**. `DREAM_TIME_OF_DAY` sets the
world clock's *start* tick and the clock then runs, so the sun moves during the scene's own wait and
two runs a few ticks apart have every shadow in a different place. That diagnosis was right.

**The fix applied here was not.** The capture harness has a `tod:` step that pins the *client* time
of day, and each scene was given one — but `tod:` takes decimal **hours** in [0, 24) and these were
written as minutes (`tod:720`, `tod:1320`). `TryParseTimeOfDay` rejects anything at or above 24, so
the step never parsed and the sun was never pinned. The evidence was in the output the whole time
and was not read: the re-recorded baselines kept their `04-snap` and `09-snap` numbering instead of
shifting by one, which a step that actually ran would have caused. The measured improvement
(wolf-staged's worst block 0.1118 → 0.0531) therefore belongs to the widened ignore box and to
run-to-run variance, not to a pinned sun.

Master's `integrate/gfx-quickwins` fixed it properly and in parallel: `tod:12` / `tod:22` in hours,
plus `DREAM_FIRE_CLOCK=10` to pin the flicker and `DREAM_DAY_LENGTH_MINUTES=1440`. Master's scenes
and baselines were taken wholesale at the merge and this branch's discarded.

The second cause found here still stands: wolf-staged drives the world with `dev:` commands and the
server echoes them into a panel whose height depends on how many lines have arrived, so its top edge
moves between runs. Whether master's scenes need the wider ignore box is settled by the re-record
below rather than assumed.

The wave itself was **measured** against the gate rather than assumed innocent. Four runs of
wolf-staged on one build, two with the post chain and two without: pixels over the scene's own 0.2
threshold were 0.0206% (post on, run to run), 0.0157% (post off, run to run) and 0.0098% (on versus
off). The chain's effect on that frame is *smaller than the scene's own run-to-run noise*, so the
pipeline was not what the gate was catching. That measurement does not depend on the `tod:` mistake.

## Verification

- `dotnet build dream\dream.csproj`: 0 errors.
- Full suite on the integrated branch: **Passed 10226 / Failed 0 / Skipped 47 / Total 10273**.
- `ShaderCompileTests` compiles and links all 18 programs in a hidden GL context, the three new post
  programs and the new `surface.glsl` include among them.
- `enginelook.py`: `contract: 23 lines match the shaders`, PASS.
- `check_char.py`: 0 problems.
- In-engine captures at noon and night, a window-resize run (1440 → 1600×1800 → 1024×768, rebuilding
  the scene target each time) and an ImGui-overlay run: no artifacts, HUD and dev overlay unaffected
  because both are drawn after the resolve.

## Open items

- `mean saturation` as above: our dark pixels are tinted. The next colour pass, deliberately separate.
- `wolf-staged` still flakes by about one marginal block. The honest fix is a capture-only world
  clock freeze; the client-side `tod:` pin got most of it without touching the simulation.
- Redundant GL state and static-chunk batching were **measured and left alone**: in the sampled frame
  static timing was 0.00 ms across the board with 108 meshes reused and 0 rebuilt, and no before/after
  counter evidence was available for the state sets. One stationary spawn view is not a stress case,
  so this is "not shown to be a problem" rather than "shown to be fine".
- `cubeFragment.glsl` still has no material response at all — the glTF `metallicFactor` and
  `roughnessFactor` are uploaded and read by neither world shader. Turning one on would change every
  prop and terrain pixel and needs a uniform from `Rendering/Core`; it should call `surface.glsl`.
- `WeaponMaterial.Arcane`'s emissive (0.30, 0.12, 0.55) peaks below the knee and therefore never
  blooms. Raising it to (0.55, 0.22, 1.00) was proposed and not applied, because it also brightens
  the sub-knee part and wants `check_char.py` run against it.
