# Graphics quick wins

Date: 2026-11-19. Branch `integrate/gfx-quickwins`. Source: the quick-wins wave in
`docs/ledger/analysis/2026-11-19-graphics-engine-review/README.md`.

## What changed

- **Moon shadows.** The shadow map was already rendered from the moon at night, but only the sun term read it, so the
  night pass was wasted. The map's visibility now multiplies the term of the light it was rendered from
  (`dreamShadowMapIsSun` in `lighting.glsl`; `clouds.glsl`, `enemyFragment.glsl`; `enginelook.py` restates it). By day
  the output is bit-identical. `dreamDirect` is unchanged.
- **Frame pacing.**
  - VSync is a setting: `vsync=off|on|adaptive` in `desktop-client.txt` (default adaptive), `DREAM_VSYNC`, and a menu row.
  - OpenTK's VSync stays Off. OpenTK 4.9.4's Adaptive is a software toggle that turns VSync off whenever a frame
    exceeds 1/UpdateFrequency, so `Game` sets `GLFW.SwapInterval` itself: -1 when the driver has
    `swap_control_tear`, else 1.
  - Capture runs are always Off. The profiling launcher sets `DREAM_VSYNC=off`, so perf numbers stay comparable.
- **GL context.**
  - The client asks for 4.5 core and falls back to 3.3 core. A hidden 1x1 probe window is the only thing ever
    retried; it swaps in a recording GLFW error callback, so a refused context does not leak a window.
  - `DREAM_GL_VERSION=3.3` skips the probe. No feature uses anything above 3.3.
- **Textures.**
  - Every TexImage uses sized `Rgba8`.
  - Mipmapped world, model and terrain textures get 8x anisotropic filtering (`DREAM_ANISOTROPY`), clamped to the
    driver.
- **Terrain.** The opaque static terrain pass is back-face culled. The double-sided reason (first-person cameras) is
  obsolete, and every chunk face is wound counter-clockwise.
- **Models.**
  - glTF normal, metallic-roughness and occlusion maps are no longer decoded, uploaded or bound, because no shader
    read them.
  - The joint palette is uploaded once per (node, program) per render call instead of once per primitive.
- **Lights.**
  - Past 16 in view, kept lights fade by distance to the nearest light that was not kept, so a rank swap no longer
    pops.
  - The identical second FrameLighting upload per frame is skipped.
  - The `DREAM_DEBUG_POINT_LIGHTS` ring is now a spiral, so it exercises a real cut-off. It used to tie every light.
- **GPU debugging.**
  - `GpuDebug` pushes a KHR_debug group per pass ("Shadows", "World", "Actors", "Boundary void", "Effects",
    "Debug overlays", "View model", "HUD"; "Sun shadow map" and "Fire shadow atlas" inside the caster pass) and
    labels every shader program.
  - `RenderDocHook` attaches the RenderDoc in-app API before the first context.
  - Markers are on in DEBUG or under RenderDoc; `DREAM_GL_MARKERS` forces them.

## How

Built with the parallel-agent-delivery workflow:

- An architect wrote the spec with six disjoint tasks, and six developers built them in parallel worktrees.
- A QA gate re-ran each task. Four went back once:
  - a circular negative test;
  - a non-ASCII label length;
  - an anisotropy test that could not fail;
  - OpenTK's Adaptive VSync tearing, plus a fallback retry that covered the whole Game constructor.
- The Lead merged the tasks and wired the debug groups and startup hooks.
- Two domain reviews and two fix loops followed.

## Verified

- **Full suite on `ebabb88e`:** 10,205 passed, 0 failed, 47 skipped. One earlier run caught two source-pin tests broken
  by the Lead's debug-group wiring; they were fixed by keeping the pinned calls at their indentation.
- **Capture log:**
  - `gl: requested 4.5 core, got 4.5.0 NVIDIA 617.42`;
  - with `DREAM_GL_VERSION=3.3`, `got 3.3.0` and the same picture;
  - `vsync: off (capture run)`, `anisotropic filtering: 8x`, `gl markers: off` (Release, as designed).
- **Golden scenes: still red, baselines NOT re-recorded.**
  - The noon and wolf baselines already failed on master before this wave, at the held torch (640,464). They went
    stale when the torch fix landed.
  - On the night scene, this wave's differences are the brazier flame flicker and moon shadows along the fence.
  - A re-record from this branch (18 runs; 36 for wolf-staged) passed `run.ps1`, but it broke two of the gate's own
    negatives in `tools/golden/tests/test_golden.py`: a x0.8 darkening passed on spawn-night 04-snap, and removing the
    wolf-scene player fell to 1.14x the limit (2x required). The recorded noise is dominated by a fire-shadow wedge
    near the Quartermaster that appears in some runs and not others, on master too. So the re-record was discarded
    and the committed baselines are unchanged.
  - Making the fire-shadow slots deterministic is the prerequisite for any useful re-record.

## Not verified

- A driver that refuses 4.5 (the fallback was exercised only by forcing 3.3).
- Adaptive VSync on a real 60 Hz display.
- RenderDoc attached.
- `UniformUploads` before and after in a perf log.
