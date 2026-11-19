# Dream renderer review: frame pipeline, lighting, shadows, sky, fog/mist, colour

Checkout: `G:\workspace\projects\dream\.claude\worktrees\integrate-torch-flame`. Paths below are relative to `dream/` unless they start with `docs/`.
Tags: **[V]** Verified (I read the code), **[D]** Derived (follows from the code), **[I]** Inferred (my judgement).

---

## 1. Area map and per-frame flow

**Files that matter**

| File | Role |
|---|---|
| `Client/Scenes/PlayScene.Render.cs` | The actual frame orchestrator: pass order, clear, GPU timer sections |
| `Rendering/Core/Render.Lighting.cs`, `Render.PointLights.cs`, `Render.Shadows.cs` | Uploads the 4 UBOs, selects lights, runs the shadow pass |
| `Rendering/Lighting/FrameLighting.cs` + `FrameLightingBuffer.cs`, `LightList.cs` + `LightListBuffer.cs`, `LightSelection.cs` | std140 mirrors: FrameLighting (256 B, binding 0), LightList (528 B, binding 1); world and view-model ranges |
| `Rendering/Shadows/ShadowMapTargets.cs`, `ShadowCasterPass.cs`, `SunShadowVolume.cs`, `FireShadowAtlas.cs`, `ShadowSettings.cs` | The only offscreen targets in the engine: sun depth map plus the fire atlas |
| `Rendering/Sky/SkyModel*.cs` | Time of day mapped to a FrameLighting (pure CPU) |
| `Rendering/Mist/WarMistRenderer.cs`, `BoundaryMistVoidRenderer.cs` | Translucent slice veil, plus a fullscreen smoke triangle at depth 1 |
| `shaders/lighting.glsl`, `clouds.glsl`, `pointLights.glsl`, `fireShadows.glsl`, `boundaryMist.glsl` | The shared lighting model, `#include`d (one level deep) by `cubeFragment.glsl` and `enemyFragment.glsl` |

**Actual pass order** (`PlayScene.Render.cs:48-264`) [V]:
1. `SkyModel.Evaluate`, then `SetFrameLighting` (:48-49). Clear the default framebuffer to horizon × 0.25 in isometric (:53-55).
2. `CollectFrameLights` (:86).
3. **Shadow pass, offscreen** (:93, `Render.Shadows.cs:54-97`): the 2048² `DEPTH_COMPONENT32F` sun map (one full caster submission), then up to 4 fires × 4 faces = 16 tiles of 512² in a 2048² `DEPTH_COMPONENT24` atlas. Each tile is another full caster submission (`ShadowCasterPass.cs:81-114`).
4. Back on the **default framebuffer** (MSAA 4x by default, `MsaaSettings.cs:13`, `Game.cs:103`): `DrawWorld` uploads the UBOs (`Render.Lighting.cs:197-210`) and draws the terrain, chunks and dynamic cubes. Then props, enemies, the local player, remote players and economy entities (:102-156).
5. The sky pass, **only when not isometric** (:162-165). Isometric is the only reachable view, so in shipping the sky pass never runs.
6. The boundary void, a fullscreen triangle (:169-172).
7. The effects pass: war-mist slices first, then the LDR fx particles (:174).
8. Debug overlays, the view model (first person only), then HUD and ImGui (:219-263).

**Render targets** [V]: everything visible goes forward into the default framebuffer. The only FBOs in the codebase are the two depth-only shadow FBOs (`git grep GenFramebuffer`: only `ShadowMapTargets.cs:245`). There is no colour render target, no `BlitFramebuffer`, and no renderbuffer. The MSAA resolve happens implicitly at SwapBuffers (`Render.Lifetime.cs:125-130`).

**Colour pipeline** [V]: albedo textures are uploaded as `GL_RGBA`, not `SRGB8_ALPHA8` (`TextureDecoder.cs:66,100,186`; `StaticTerrainTileArray.cs:244`). `GL_FRAMEBUFFER_SRGB` is never enabled, and no shader contains a gamma `pow` (grep for `srgb|gamma|tonemap` gives no hits). So **all lighting arithmetic runs on gamma-encoded values**, and the result is written straight out as display values. Exposure (`Params.x`) is 1.0 in every keyframe (`SkyModelKeyframes.cs:178-239`). Output is `fog(expose(lit) + emissive)` (`cubeFragment.glsl:434`), and the 8-bit UNORM framebuffer hard-clips it at 1.0.

**Light model** [V]: forward rendering. Every world fragment computes hemisphere ambient, sun plus moon (`lighting.glsl:19-23`), a 4-octave cloud-shadow fBm (`clouds.glsl:477-517`), a 9-tap sun PCF, and a loop over up to 16 point lights from a UBO (`pointLights.glsl:91-108`). The fire-shadow lookup runs only when `f > 0`: a slot search plus 9 hardware-PCF taps (`fireShadows.glsl:181-258`). Every fragment also gets boundary-mist opacity and exp2² fog. The CPU keeps the 16 lights nearest the focus whose spheres intersect the view frustum (`LightSelection.cs:62-120`).

**Shadows** [V]: the sun uses one cascade. It is an orthographic map over a fixed 40 × 40 × 26 receiver box around the focus, with a constant texel of about 2.3 cm, texel-snapped (`ShadowSettings.cs:298-359`, `SunShadowVolume.cs:35-58`). Filtering is 3×3 taps, each a hardware 2×2 comparison. Bias is normal offset plus slope-scaled, defined in texels. Fires use a 4-face omni atlas (±X, ±Z; the ±Y cones are unshadowed) with a gutter and clamped taps. Fire casters are re-rendered every frame because the flicker moves the light.

**Anti-aliasing** [V]: MSAA 0/2/4 on the default framebuffer, default 4. No alpha-to-coverage, so MASK cutouts stay aliased; this is documented at `Render.Lifetime.cs:126-129`. There is no FXAA, SMAA or TAA.

**Post-processing** [V]: none. There is no bloom, tonemap, colour grading, AO, DOF, vignette or dithering. Fog is per-fragment, inside the forward shaders.

---

## 2. Antipatterns, ranked by consequence

### A1. Lighting is computed in gamma space, and there is no HDR target or tonemap. This is the quality ceiling.
- **Evidence** [V]: `TextureDecoder.cs:66,100,186` and `StaticTerrainTileArray.cs:244` upload `Rgba` (UNORM, not sRGB). No `FramebufferSrgb` exists anywhere. The comments call colours "linear" (`cubeFragment.glsl:23`; `docs/rendering-and-assets.md` "linear colour already multiplied by intensity"), but nothing linearises them. The final write is `cubeFragment.glsl:434` into an 8-bit default framebuffer.
- **Consequence** [D/I]:
  1. Anything over 1.0 hard-clips per channel. A brazier-lit orange surface clips red first and hue-shifts toward yellow or white. That clipping is the whole reason for the "albedo ≤ 0.40" rule (0.46 × 2.17 = 1.0): the artists are paying for a missing tonemap curve with 60% of their albedo range.
  2. Lambert, falloff and light addition run on gamma-encoded values, so two overlapping pools add too bright and falloffs look wrong. The `1/(1+1.5s)` term in `pointLights.glsl:100` ("so a pool punches") is a hand-tuned patch for a perceptual curve that a linear pipeline gives for free.
  3. Texture filtering, box mips (`TextureDecoder.cs:141-166`), MSAA resolve and additive fx blending (`(One, OneMinusSrcAlpha)`) all average gamma values. Mips and silhouettes darken, and dark-on-bright edges look thin.
  4. Every post effect is blocked: with no colour target, bloom, grading and AO have no input.
- **Fix sketch**: Step 1 (M) is an RGBA16F multisampled colour renderbuffer plus a depth renderbuffer as the world target. Resolve it with `glBlitFramebuffer` to a single-sample RGBA16F texture, then run one fullscreen "present" pass. That pass starts with a tonemap tuned close to identity below about 0.6 (so the art gates barely move), followed by a soft shoulder to 1.0 and 8-bit dither. The HUD and ImGui then draw onto the default framebuffer as now. Step 2 (L, a separate wave) moves to sRGB textures and a linear-space shader, with `FRAMEBUFFER_SRGB` or a gamma encode at present. Every authored colour, `enginelook.py` and `GrimLookHistogramTests` would need retuning; do it only once the tonemap is in.
- **Effort**: M for HDR + tonemap + dither. L for full linear.

### A2. At night the sun shadow pass renders from the moon, but nothing ever reads it.
- **Evidence** [V]: `Render.Shadows.cs:103-111` builds the map for the dominant light, which is the moon at night, with strength 1. The shader applies visibility only to the sun term (`clouds.glsl:516-517`; also `enemyFragment.glsl:356`), and the sun's intensity is 0 at night. The moon term is never shadowed (`docs/ledger/wave-shadow-maps.md:670`, "The moon is never shadowed"). The ledger documents the moon limit, but not the fact that the pass still runs.
- **Consequence** [D]: for the whole night (roughly 20:00–05:00, the hours the grim look is built around), every frame pays one full caster submission. By the ledger's own estimate that is +80–130 draws on a frame that is CPU-bound on submission. Every world fragment also pays 9 shadow taps (18 on equipment) whose result is multiplied by zero. That cost buys nothing.
- **Fix sketch**: either (a) multiply the moon term by `dreamSunVisibility` too. This is one token in `clouds.glsl:517` and `enemyFragment.glsl:357`, and it gives moonlit shadows, which is a real mood win. Or (b) return `ShadowFrame.None` for the sun half when `SunDirection.W == 0`. The ledger says (a) would need the GrimLook CPU mirror to gain a visibility term, so choose (a) with that test update, or (b) as the zero-risk option.
- **Effort**: S.

### A3. Up to 17 full scene submissions per frame for shadows, on a CPU-bound frame
- **Evidence** [V]: `ShadowCasterPass.cs:81-114` covers the sun plus 4 slots × 4 faces, and each calls `DrawShadowCasters`. That method walks every caster renderer: world, props, scatter, enemies, players, NPCs and ground items (`Render.Shadows.cs:162-171`). The default is 4 casting fires "BY EXPLICIT DECISION" (`ShadowSettings.cs:273-277`). Nothing about the sun map is cached between frames, even though static chunks and the sun direction barely change frame to frame.
- **Consequence** [D]: the ledger estimates +1.5–3.0 ms CPU for 2 fires (`wave-shadow-maps.md:429-431`), and 4 fires doubles the fire half. This is the largest single CPU line in the frame. It is also the reason the design cannot afford more casting fires, so a brazier field shadows inconsistently and pops (`wave-shadow-maps.md:594`).
- **Fix sketch**: (1) Cache the sun map's static layer. Re-render static chunks, props and scatter only when the texel-snapped origin changes or the sun has rotated more than ε. Otherwise `glBlitFramebuffer` the cached depth (GL 3.0) into the live map and draw only the actors and dynamic cubes. (2) Fire faces cannot be cached while the flicker moves the light, but the casters per face can be pre-culled once per slot with a sphere-of-radius test instead of four frustum walks. (3) Longer term, multi-draw-indirect would let one submission cover the casters (needs GL 4.3; macOS caps at 4.1).
- **Effort**: M for (1).

### A4. Hard 16-light cap with nearest-first selection and no fade
- **Evidence** [V]: `LightList.MaxLights = 16`, a fixed UBO array (`pointLights.glsl:85-89`). `LightSelection.cs:85-88` evicts the farthest light outright, and flash lights only fill free slots. Fire-shadow slot choice is a ranking with binary strength (`FireShadowAtlas.cs:87-140`; ledger §8.2 "can pop").
- **Consequence** [D/I]: in a village with about 13 braziers, plus torches on several players, plus hit flashes, the 17th light makes a whole pool switch on or off as the focus moves. A fire also gains or loses its shadow when the slot ranking changes. In a dark game where light is the main readability tool, a pool turning off is the most noticeable artifact there is.
- **Fix sketch**: S option: carry a per-light weight that fades over about 0.25 s when a light leaves or enters the set (keep 2–4 spare slots for lights that are leaving), and fade fire-shadow strength the same way. M option: clustered forward lighting (see L3).
- **Effort**: S for the fades, M for clustered.

### A5. No dithering on an 8-bit target whose night palette lives in its bottom 5% of code values
- **Evidence** [V]: the night values are horizon 0.026, ambient 0.006–0.048 (`SkyModelKeyframes.cs:116-120`), and mist bases 0.016–0.10 (`boundaryMist.glsl`). The output is UNORM8 with no gamma lift, and there is no dither anywhere.
- **Consequence** [D]: 0.026 × 255 ≈ 6.6, so the night horizon, the smoke gradients and the war-mist falloff are drawn with a handful of code values. That shows as contour banding exactly in the slow, smooth gradients (the mist, the torch pool edges) that the art direction relies on.
- **Fix sketch**: add ±0.5 LSB triangular or blue-noise dither at the end of the world shaders now. With A1 in place, put it in the present pass.
- **Effort**: S.

### A6. glTF normal, metallic-roughness and occlusion maps are uploaded and bound, but no shader reads them
- **Evidence** [V]: `AnimatedModel.cs:164-177` uploads all five textures, and `AnimatedModel.cs:636-651` binds units 1–4 and sets `material.normalTexture`, `.metallicRoughnessTexture` and `.occlusionTexture`. No `.glsl` file declares them (grep for `occlusion|normalTexture|metallicRoughness` in `shaders/` hits only a comment in `depthFragment.glsl`).
- **Consequence** [D]: wasted VRAM, plus extra `ActiveTexture`/`BindTexture` calls per material bind on a submission-bound CPU, for any asset that ships these maps. It also misleads: an artist who adds a normal map gets no change. Units 2 and 3 are also the terrain samplers' units (`docs/ledger/wave-shadow-maps.md:329`), so this is a latent binding clobber if terrain stops rebinding them [I].
- **Fix sketch**: either stop uploading and binding the three unused maps, or wire up the occlusion map now (multiply the ambient term; S) and add normal maps later with tangents (M).
- **Effort**: S.

### A7. Distance fog is a no-op in the isometric view, but every fragment still pays for it
- **Evidence** [V]: fog starts 12–18 m from the focus (`SkyModelKeyframes.cs:110,174-235`). The isometric ground footprint is about 16 × 23 m (`ShadowSettings.cs:298-300`), so the farthest visible ground is about 14 m from the focus. At night (start 12, density 0.058) transmittance at 14 m is ≈ 0.99 [D]. The isometric clear colour is horizon × 0.25, but the fog colour is horizon × 1 (`PlayScene.Render.cs:53` vs `lighting.glsl:30`), so the two disagree wherever fog does apply.
- **Consequence** [D]: the scene has no atmospheric depth cue at all. Isometric ARPGs get depth from height fog and grading, not distance fog. The per-pixel cost is small. The real cost is that the fog knobs suggest a cue that does not exist.
- **Fix sketch**: replace or augment the fog with height fog (density by world Y relative to the floor, plus a distance-from-focus term retuned for 8–14 m), and use the clear colour as the fog colour in isometric.
- **Effort**: S.

### A8. Every fragment pays for clouds, boundary mist and both shadow functions, whatever it covers [I, low]
- **Evidence** [V]: `clouds.glsl:511-515` runs 4-octave value noise (16 hashes) per world fragment whenever coverage > 0. `dreamApplyBoundaryMist` does branch out on opacity 0 (`boundaryMist.glsl:164-169`), which is good. The equipment path evaluates the sun PCF twice (`enemyFragment.glsl:310` via `dreamDirectShaded`, and :356) and the fire PCF twice (diffuse and `dreamPointSpecular`, `pointLights.glsl:104,139`).
- **Consequence**: today the GPU is nearly idle (world pass 0.13 ms, ledger :47), so this costs nothing. It becomes real at 4K or with an HDR target and post-processing. Treat it as a note, not a priority.
- **Fix sketch**: compute sun and fire visibility once in `enemyFragment` and pass them into the specular functions. The cloud mask could be a 256² texture rendered once per frame, or baked into the shadow map's projection.
- **Effort**: S.

### A9. FrameLighting is uploaded twice per frame into the same buffer range [V, low]
- **Evidence**: `Render.Shadows.cs:79` uploads, the shadow pass draws reading block 0, then `Render.Lighting.cs:202` does `BufferSubData` on the same bytes.
- **Consequence** [I]: the driver must either rename the buffer or wait for the shadow draws to finish. That is usually a hidden copy, occasionally a stall. It is small, but it sits on the critical CPU path.
- **Fix sketch**: give the shadow pass its own range, or skip the second upload when the bytes are identical.
- **Effort**: S.

### A10. The docs' frame order is stale [V, maintainability]
`docs/rendering-and-assets.md:17-27` lists the sky as step 5 and leaves out the shadow pass, props, the boundary void and the war mist. In isometric (the only reachable view) the sky is skipped (`PlayScene.Render.cs:162`), and `SkyRenderer`'s discs and stars are dead in shipping. Only `SkyModel` lighting and the cloud shadows are live.

---

## 3. Level-ups, ranked by visual payoff per effort for this codebase

| # | Technique | What it unlocks here | Cost / risk | GL |
|---|---|---|---|---|
| L1 | **HDR scene target + present pass**: MS RGBA16F renderbuffer, blit resolve, fullscreen tonemap (soft-shoulder Reinhard or a filmic curve tuned to identity below about 0.6), dither | Removes the hard clip and the hue shift on fire-lit surfaces. Starts lifting the 0.40 albedo ceiling. Fixes banding (A5). It is also the prerequisite for L2, L4 and L6. | M. Risk: every art gate shifts slightly, so tune the toe to identity and re-measure the gates. VRAM about 40 MB at 1440×1024 with 4x MSAA. Fill +0.1–0.3 ms. | 3.3 (`glRenderbufferStorageMultisample`, float textures, `glBlitFramebuffer` are core 3.0) |
| L2 | **Bloom** on the HDR buffer: threshold, 5–6 level downsample/upsample (dual Kawase or the CoD-style 13-tap) | Braziers, torches, embers, ember veins and spell fx glow for real. Today's additive halo quads are a fake of this. This is the biggest mood win for a night-lit gothic ARPG (Diablo IV and PoE lean on it heavily). | S once L1 exists. About 0.2–0.4 ms GPU. | 3.3 |
| L3 | **Rim / fresnel light on actors** in `enemyFragment` (view-dependent, tinted by the nearest light or the sky ambient) | Directly addresses actor legibility (the A3 lighting gate): a dark figure on dark ground gets a readable silhouette. This is the standard Diablo and Hades trick. | S. One term. Needs the eye position, which `uEquipEye` already provides for equipment. | 3.3 |
| L4 | **Ambient occlusion**: (a) per-vertex voxel corner AO baked into static chunk meshes by the mesh builder (the Minecraft-style 3-neighbour rule), (b) the glTF occlusion map on the ambient term (A6), (c) later, SSAO/GTAO at half resolution from the resolved depth | Today a shadowed surface drops to flat ambient (ledger §8.1). Voxel AO grounds every cell edge, wall foot and crevice for zero runtime cost, and it fits a voxel world exactly. | (a) S–M (the worker-thread mesh build already exists), no GPU cost. (c) M, about 0.5–1 ms, needs L1's depth. | 3.3 (SSAO without compute) |
| L5 | **Lights inside the war mist and smoke**: add `dreamPointLights(up, p) × k` to `warMistFragment.glsl:63` and to `dreamBoundarySmoke` | Braziers and torches glow through the veil, which reads as volumetric for the price of a loop. Today the veil ignores every point light (it uses ambient + direct only). Big atmosphere payoff. | S. 16 lights × 6 slices per pixel, cheap at this resolution. | 3.3 |
| L6 | **Colour grading LUT** (a 32³ 3D texture in the present pass, authored in a paint tool from a screenshot) | Lets the grim palette be graded globally (crushed shadows, cold mids, warm fire) instead of authored per material against gates. It can replace some of the hand-tuned albedo ceilings. | S once L1 exists. | 3.3 (3D textures are core) |
| L7 | **Clustered forward lights**: a froxel or 2D-tile light index in a TBO (`samplerBuffer`) or a large UBO, built on the CPU per frame | Removes the 16-light cap and the pop (A4). Makes spell lights, projectiles and many flashes affordable. | M. The CPU builds a small grid (isometric depth range is shallow, so 2D screen tiles × 4 depth slices is enough). | 3.3 (TBO core in 3.1) |
| L8 | **Moon shadows** (A2 fix (a)) | Moonlit cast shadows at night. The pass is already being paid for. | S plus a GrimLook mirror update. | 3.3 |
| L9 | **Static-layer shadow caching** (A3 fix) | Frees about 1–2 ms CPU, which buys more casting fires (8 fit by atlas re-layout) or a second sun cascade. | M. | 3.3 (blit). `glCopyImageSubData` is 4.3, not needed. |
| L10 | **Soft particles** (fade fx by the scene depth difference) | Removes the hard lines where smoke, ash and halo quads cut into the ground. | S after L1 (needs a resolved depth texture). | 3.3 |
| L11 | **Anisotropic filtering** (`GL_EXT_texture_filter_anisotropic`, 8x) on terrain tiles and models | Side faces of the cells, and ground toward the top of the screen at a 50° pitch, stay crisp instead of blurring. | S. One `TexParameter`. | Extension everywhere; core in 4.6. macOS supports the extension. |
| L12 | **Alpha-to-coverage** for MASK materials | Smooth cutout edges on contact discs, decals and future leaf cards under the existing 4x MSAA. | S (`GL_SAMPLE_ALPHA_TO_COVERAGE`, plus output alpha instead of discard in the MASK variant). | 3.3 |
| L13 | **Height fog / low ground mist** (A7) | A depth cue in isometric: low-lying hollows fill with murk. Pairs with L5. | S. | 3.3 |
| L14 | **Normal maps** on models and terrain tiles (tangents from MikkTSpace, or derivative-based cotangent frames in the shader to avoid a vertex-format change) | Surface detail under moving torchlight, which a light-driven look gets the most from. | M (asset pipeline). Derivative TBN needs no new attribute. | 3.3 |
| L15 | **Decals** (blood, scorch, rune circles) projected in a deferred-decal style using the depth from L1 | A genre staple (Diablo, PoE) for combat feedback and grime. | M. | 3.3 |

Not recommended now: TAA (the engine has no history buffer and MSAA already covers geometry; ledger §8.8 agrees). Full deferred shading (forward plus clustered fits a GPU-idle, MSAA-on engine better). PCSS (ledger §1 reasons it out correctly). Compute-based anything: it needs GL 4.3, which locks out macOS at 4.1.

**Recommended order**: A2 (S) → L3 rim (S) → L5 lights in the mist (S) → L11 aniso + A5 dither (S) → **L1 HDR/tonemap (M)** → L2 bloom (S) → L6 LUT (S) → L4a voxel AO (S–M) → A4 fades / L7 clustered → L9 shadow caching.

---

## 4. Done well

- **UBO contracts**: std140 mirrors with tests pinning layout, member order and the GLSL text (`FrameLighting.cs:6-38`). The world and view-model ranges avoid per-draw uniform churn, and uploads are zero-alloc from the stack (`FrameLightingBuffer.cs:112-126`).
- **Sun shadow math**: a fixed receiver box gives a constant texel, the matrix is texel-snapped, normal offset moves the lookup rather than the depth, and the slope bias is defined in texels so it holds at every resolution (`ShadowSettings.cs:314-336`, `lighting.glsl:56-81`). The `CLAMP_TO_BORDER` 1.0 means outside the volume reads as lit.
- **The fire atlas is carefully engineered**: gutters plus clamped taps mean no tile bleed. Every face is a plain LookAt rather than a mirrored view, documented for future front-face culling (`FireShadowAtlas.cs:23-31`). The near plane is derived from the flicker wander.
- **Robust state handling**: a 1×1 dummy depth texture keeps the shadow samplers valid with shadows off (`ShadowMapTargets.cs:10-12`). Shadow state is saved and restored in a finally. `DREAM_SHADOWS=0` returns a literal 1.0 with no fetch.
- **GPU timer**: a 4-slot query ring read 2 or more frames late, so it never stalls (`GpuFrameTimer.cs:54-62`).
- **Long-session safety**: noise clocks are wrapped in double on the CPU, with periodic lattices, so nothing jumps (`clouds.glsl:442-455`, the boundary-mist clocks).
- **Stable light selection**: ties keep input order, so lights do not flicker (`LightSelection.cs:6-11`).
