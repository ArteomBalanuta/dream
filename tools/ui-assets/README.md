# UI art generators

Python scripts that generate dream's UI art into `dream/ui/`. All art is original and procedural, with fixed seeds. Output is byte-deterministic.

## Setup

Python 3.13 with two packages, both pinned in `requirements.txt` (numpy was already a dependency of `uiart.py`; `forge.py` adds none):

```bash
python -m venv .venv
.venv/Scripts/python -m pip install -r tools/ui-assets/requirements.txt   # numpy==2.5.4, pillow==13.0.0
.venv/Scripts/python tools/ui-assets/build_all.py
```

If pip cannot reach an index, point system Python at any directory that already holds those two versions, read-only, for example `PYTHONPATH=<venv>/Lib/site-packages python tools/ui-assets/build_all.py`. No Blender, scipy or GPU is used.

Each `gen_*.py` exposes `main(out_root)` and writes only its own directory. Each `tests/check_*.py` exits nonzero on failure.

Atlases are straight-alpha RGBA PNGs at 4 px per logical unit with a JSON sprite table. Rects exclude the extrusion. `yDown` is true, so the loader must not flip V. The spec lives in `docs/superpowers/specs/2026-11-01-classic-mmo-ui/`.

`gen_particles.py` writes the effects atlas `dream/ui/particles/particles.png` and `particles.json`. It is a fixed grid, not a shelf-packed atlas: 512x256 RGBA8, 8 columns by 4 rows of 64 px cells, frame 0 top-left, rows downward, loaded with no flip. Each cell keeps a 4 px margin that is fully zero. RGB is premultiplied emission and stays meaningful where A=0 (A=0 draws purely additive); A is coverage. Frames: 0-3 sparks (one straight streak along +X, head at +u, near-neutral, A=0), 4-7 embers (near-neutral nucleus, peak at least 200, A=0; the particle tint sets the heat), 8-11 dust motes (cold near-neutral, RGB peak about 215, A=0), 12-15 ash (soot RGB about 0.30 of A, A>0), 16 soft halo, 17 tighter halo (A=0, peaks about 194 and 240, no clipped plateau), 18-21 flame tongue (baked ember-to-gold ramp, A=0), 22 contact blob (RGB=0, A>0, darkens), 23-31 reserved and empty. The PNG must stay at most 32 KB. `tests/check_particles.py` gates all of it, and `previews/particles.png` is the review sheet.

`items_mask.png` is a data texture: RGB stays meaningful where A=0, so load it raw, with no premultiply and no alpha stripping.

## forge.py: the renderer for forged art

New art is lit in linear light, not by painting sRGB values. The chain is

```
Relief (form + material ids) -> bake -> age -> light -> finish -> downsample
```

```python
import forge
from forge import Relief, Rect, Capsule, Poly, Ellipse

r = Relief(120, 48, "iron", 1.0)                          # canvas in u; 16 px/u hi-res
r.plate(Rect(3, 4, 46, 40, 5), z=1.3, bevel=2.6, profile="rolled", rise=0.5,
        mat="iron.warm", wobble=0.14, seed="my.plate")      # plate top = z + bevel * rise
r.pocket(Rect(12, 13, 23, 21, 3.4), depth=3.4, wall=1.1, mat="void")
r.dome(7.2, 8.0, 1.8, z=1.3, mat="iron.rivet")             # forged rivet, adds a rust drip
rgba = r.render("my.sprite", wear=.3, grime=.5, rust=.2, dust=.1)   # float sRGB RGBA, hi-res
sprite = forge.downsample(rgba)                            # uint8 RGBA at 4 px/u, goes to uiart.Atlas
```

| Function | Contract |
|---|---|
| `bake(height, primary=None, key=None, wrap=False) -> Maps` | `normal ao shadow cavity curvature edge`. `edge` comes from `primary` only (the authored form), so wear follows the form, not the noise |
| `age(s, m, seed, wear, grime, rust, dust, streak=None, origin, period, wrap) -> Surface` | edge wear, soot, rust, dust, file marks; also writes `Surface.worn` |
| `light(s, m, rig=GOTHIC_RIG) -> ndarray` | linear HDR: anisotropic GGX with Schlick Fresnel, baked AO and cast shadow, reflection panels |
| `finish(hdr, alpha, exposure=1.0, value_map=None) -> ndarray` | bloom from values over 1, composition multiply, warm highlights over cold shadows, ACES-style curve; float sRGB RGBA |
| `downsample(rgba, factor=SS, sharpen, wrap="", dither=True) -> uint8` | premultiplied linear-light Mitchell, light unsharp, triangular dither at the 8-bit rounding (the dither lives here because averaging 16 samples would erase it earlier) |

`Relief` keeps gen_hud's `plate`, `mask_plate` and `sweep` signatures but takes a material name `mat` and an optional `tint` instead of albedo and gloss. It adds analytic shapes (`Rect`, `Ellipse`, `Poly`, `Capsule`), `pocket`, `dome`, `drip`, `wobble` and `nick` for uneven outlines, and `render(..., debug=dict)` to hand back the maps and surface.

Materials (`forge.MATERIALS`): `iron`, `iron.warm`, `iron.cold`, `iron.black` (the field plates sit on), `iron.rivet`, `steel`, `brass`, `rail` (ivory-gold inlay), `ivory`, `stone`, `void`, `rust`. They are linear numbers: blackened iron has F0 about 0.03 and rough 0.55, bare steel F0 0.30 and rough 0.40, so highlights are broken sheens and a few hot glints rather than mirror lines. `forge.rust`, `forge.dust`, `forge.ivory`, `forge.gold` and `forge.steel.bare` come from `tokens.json`.

### Rules the art follows

- Form first. Primary shapes are 2 to 110 u; noise only adds beaten facets, hammer dents and light pitting, and the finest bands are capped by `check_art.py`.
- Wear is the convexity of the primary form times a large-scale "use" field times a broken-up threshold. Some edges stay black, others wear in patches. Never scatter it.
- Rust lives in cavities, seams and under rivets; soot in cavities; dust on up-facing slopes.
- Wear and highlights stay restrained. If edges read as chrome or as polished trim, lower `wear` or the material's bare F0 rather than adding blur.

### Tiling, big sprites and noise

All noise is a function of absolute position in u, so a band cut from a big canvas shades like the whole. `Relief(..., tile="x")` (or `"y"`, `"xy"`) makes noise, blurs, AO and shadow wrap on that axis; draw plates that cross the border twice, once at each offset; pass `wrap=` to `downsample`. `Relief.sprite_banded(seed, band_u=40, overlap_u=10)` renders and downsamples in horizontal bands. A render peaks near 320 bytes per hi-res pixel (measured), so a canvas above about 3 Mpx (roughly 110 x 110 u) should be banded; the 165 x 387 u window slab would otherwise need about 5 GB. Checked: banded against whole differs by at most 2/255.

### Cost

The 120 x 48 u sample plate (1.5 Mpx hi-res) renders in about 6.5 s on one core, 4.4 microseconds per hi-res pixel, so the cost is linear in area: a 190 x 40 u crown is about 9 s and the 165 x 387 u window slab about 75 s plus the overlap of the bands. Run the generators in parallel processes when rebuilding everything.

### Sample plate

`python tools/ui-assets/forge.py` writes `previews/forge_sample.png`: a forged-iron plate group with rolled lips, a socket, rivets and ivory-gold inlay. It is the acceptance sample for the chain and is gated by `check_art.py`.

## check_art.py: numeric art gates

```bash
.venv/Scripts/python tools/ui-assets/tests/check_art.py           # sample plate, metrics, determinism
.venv/Scripts/python tools/ui-assets/tests/check_art.py --fast    # skip the determinism double run
.venv/Scripts/python tools/ui-assets/tests/check_art.py --art --only bar,hp,mp,dragon   # measure dream/ui too
```

The gates are constants in the file, measured on our own generated art at 2.25 px/u; nothing reads an outside image. They are floors, ceilings and ratios. `--art` fails until the bar and window art are rebuilt through forge: WS4 owns `bar hp mp dragon`, WS5 owns `slab frame cell`. The sample plate must land in the bar's value gates (mean, median, p95, p99, share over .85, 4u over 0.5u band ratio), keep clipped highlights under 1.2 percent, and show wear that covers a minority of edge pixels, varies between 6 u cells and leaves a third of the edge cells clean.

`check_sources.py` also scans `tests/check_art.py` to keep it free of file reads.

## isoforge.py: isometric props (not part of build_all)

True 3D sprites at pitch 50, yaw 45, 4 px per u final, rendered 4x over. Output goes to `tools/ui-assets/iso/` (`sprites/`, `shadows/`, `iso_manifest.json`, `iso_sheet.png`), never to `dream/ui`.

```bash
python tools/ui-assets/gen_iso.py                          # everything, DREAM_ART_WORKERS processes (default min(4, cpu))
python tools/ui-assets/gen_iso.py --only rust_barrel DIR   # only those sprites and shadows, into DIR/iso
python tools/ui-assets/tests/check_iso.py [--fast] [--partial] [--measure]
```

Chain: `Mesh` -> z-buffered triangle G-buffer (world P, view normals, material, uv, tint) -> key shadow map and 20-direction dome AO over above-horizon directions (plus a depth horizon pass for contact AO) -> `forge.Maps` -> `forge.age` -> object-space rust and moss from `waves3` -> `forge.light` with `ISO_RIG` -> `forge.finish` -> blacks crushed and a soft-knee saturation cap per material -> `Scene.fx` layer -> `forge.downsample` -> `uiart.bleed_rgb`. Alpha is the hi-res coverage averaged over 4 x 4, so silhouettes are one pixel wide and hard.

- `ISO_RIG`: a warm torch key (1.00, .76, .52) with the same direction as `forge.GOTHIC_RIG.lights[0].dir`, a strong cold fill (.22, .46, 1.0 at 6.0 against the key's 18) and a cold ambient under 15 percent of the key. Lit surfaces go warm but keep material hue, shadows go cold. The grade crushes only sRGB luma under `CRUSH` (.008), so the shadow side keeps form in luma 12 to 60 and reads blue there.
- Form bands: primary 8 u and up, secondary 2 to 6 u, tertiary under 1 u. Keep `waves3`, `Mesh.displace` and `IsoMat.detail` wavelengths in these bands; the 1 to 2 u gap stays empty (the renderer's own noise follows this; `waves3` does not enforce it, so the art modules must).
- Builders: `tube`, `lathe` (profile with the solid on the left), `grid`, `box`, `convex`, `torus`, `card`; `Mesh.xform`, `merge`, `displace`. `n=None` means flat facets, otherwise normals are welded by position with a 35 degree crease.
- Each object brings its own `IsoMat` (linear light, roughness 0.65 and up except glass and emissive); `ISO_MATERIALS` only holds `"moss"`, which the moss pass and the tombstone look up by name. `sat_cap` is applied per pixel after the grade, so tint rust on a material reaches deep orange-brown by raising that material's `sat_cap` (the barrel uses .41, which lands at p99 .49 after the downsample). Rust is also its own pass: `render(rust=)` pools it in cavities of iron and lets it run down from convex edges, and raises the cap to `RUST_CAP` (.44) only inside the rust.
- Each `gen_iso_*.py` exposes `OBJECTS: dict[str, ObjectSpec]`; `gen_iso.py` skips modules that do not exist yet.
- `check_iso.py` gates are constants measured on the generated sprites: crispness, saturation (with the rust and crimson-leaf allowances), crushed shadows, upper value, 5x5 contrast, form, warm lights over a cold shadow band, chrome share, foot point, the sheet, the potion frames and the potion being the brightest thing in the set. Form is the 4 u over .5 u band energy of alpha-normalised luma blurs over the interior (opaque mask eroded 2 px), so silhouettes do not count and a flat interior with grain fails. The cold gate is the mean R - B of opaque pixels in luma 12 to 60, which must hold at least 10 percent of the object. Per-object numbers go in `OVERRIDES`, with a comment giving the reason and the measured value. Committed sprites, shadows and manifest are compared with the generators (size, pivot, frame count, durations), and files no generator makes are flagged as stale. Every gate has a self-test on a synthetic image that must fail it. `.gitattributes` keeps the manifest LF so the byte comparison holds on Windows. It sets `tempfile.tempdir` to `.iso_tmp` in the repository root so the determinism runs stay off the system drive; do not commit that folder.

`gen_menu.py` writes the front-end atlas `dream/ui/menu/menu.png` and `menu.json`: backdrop, the DREAM title, button states, panel, slider and divider. Forged like the window chrome; the backdrop is painted with numpy. About 90 s. `tests/check_menu.py` gates sizes, slices, mood (dark backdrop and panel, ember hover, dull title) and the generator source; `--determinism` also rebuilds it twice. `DREAM_MENU_CAPTURE=<dir>` on the game walks the title screen and writes a PNG per screen.
