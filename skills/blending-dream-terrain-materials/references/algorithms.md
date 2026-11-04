# Algorithms

Three passes. Pass 1 and 2 are CPU, per world cell, inside the generator (layer A).
Pass 3 is per fragment, in the shader (layer B). Each pass is useful on its own.

## Pass 1 — macro layout from fBm

Replace the modulo-hash with continuous fields, then threshold them. Two fields are
enough for Dream's nine materials:

- `elevation(x, z)` — low basins, mid fields, high ground.
- `moisture(x, z)` — a second, independently seeded field at a different frequency.

Build each as fractal Brownian motion over 3–4 octaves of gradient noise:

```
value = 0; amplitude = 0.5; frequency = baseFrequency; norm = 0
for octave in 0..octaves-1:
    value     += amplitude * gradientNoise(x * frequency, z * frequency, seed + octave)
    norm      += amplitude
    amplitude *= gain        // 0.5
    frequency *= lacunarity  // 2.0
return value / norm          // -1..1
```

`baseFrequency` is the knob that matters. At `1/48` a 56x56 startup world holds roughly
one feature per biome; at `1/8` it is noise again. Start near `1/40` of a cell and tune
against the startup world, not against a large one.

Gradient noise with no library and no allocation: hash the four lattice corners to a
unit gradient and bilinearly interpolate with a quintic fade.

```
h = seed
h ^= x * 0x27D4EB2D; h = (h << 13) | (h >>> 19); h *= 0x165667B1
h ^= z * 0x85EBCA6B; h ^= h >>> 15
```

Keep every step `int`/`uint` and `unchecked`, then map to a gradient by
`angle = (h & 0xFFFF) * (2π / 65536)`. Use `>>>` (`int` logical shift) so the result
does not depend on sign handling.

### Slope

Stone and rock belong on steep ground. The cell's own height already exists:
`ResolveRaisedLandmarkHeight(x, z)`. Take the forward difference against its four
neighbours and use the largest absolute step as the slope. That reuses the real terrain
height instead of inventing a second one, so stone lands on the actual mounds.

### Weights, then a decision

Compute a weight per material, then pick the largest. Weights make coverage tunable;
chained modulo tests do not.

```
sand  = basin(elevation) * dry(moisture)
mud   = basin(elevation) * wet(moisture)
grass = mid(elevation)   * mid(moisture)
dirt  = mid(elevation)   * dry(moisture)
stone = smoothstep(slopeLo, slopeHi, slope)
rock  = stone * high(elevation)
```

`basin`, `mid`, `high`, `dry`, `wet` are `smoothstep` ramps over the field. Add a small
per-cell dither (`hash(x, z)` scaled to about 0.03) to the weights before the argmax so
the boundary between two near-equal weights is ragged at cell scale instead of a clean
contour. That dither is what makes a voxel transition read as organic; without it, fBm
gives smooth but suspiciously circular patches.

Keep the existing overrides ahead of the weights, in this order: rim ring, spawn path
and central path, then the noise. They are gameplay-legible and must not be eroded.

## Pass 2 — stone scatter from Worley noise

fBm makes stone flow like a liquid. Discrete stone needs cellular noise.

For a point, walk the 3x3 neighbourhood of its integer cell; each neighbour cell holds
one feature point at a hashed offset inside itself. Track the two smallest distances:

```
f1 = ∞; f2 = ∞
for each of the 9 neighbour cells:
    p = cellOrigin + hashedOffset01(cell, seed)
    d = distance(point, p)
    if d < f1: f2 = f1; f1 = d
    elif d < f2: f2 = d
```

- `f1` alone gives round blobs — the stone bodies.
- `f2 - f1` is near zero exactly on the boundary between two cells: that is the crackle
  filter, the fracture lines between stones.

Then mask it: multiply the cellular output by pass 1's `stone` weight so stones cluster
on slopes and biome borders and never float in a sand flat. Unmasked Worley is the
classic mistake — evenly spaced stones everywhere, which reads as more artificial than
the modulo hash it replaced.

Use squared distance for the comparison and take the square root once, at the end, if at
all. Nine `MathF.Sqrt` calls per cell on a 100x100 world is wasted work.

## Pass 3 — height-based blending

Linear interpolation between two surfaces gives a muddy band along the seam. Materials
should interlock: sand should pool in the crevices of stone, with the stone crests
occluding the sand completely.

Given two layers with albedo `cA`, `cB`, heights `hA`, `hB` sampled from the tiles, and
blend weights `wA`, `wB` from the mesh (`wA + wB = 1`):

```
a = hA + wA
b = hB + wB
m = max(a, b) - hBias          // hBias 0.2 .. 0.4
kA = max(a - m, 0)
kB = max(b - m, 0)
colour = (cA * kA + cB * kB) / (kA + kB)
```

`hBias` is the transition width: at 0 the seam is a hard per-texel choice, at 0.4 it
softens toward a lerp. 0.25 is a good start for Dream's tiles, whose relief is already
strong. Guard the divide — `kA + kB` is zero only if `hBias` is zero and the heights tie,
but a shader that divides by it unguarded flashes NaN on exactly the seam you are trying
to fix.

Blend the albedo, before lighting. `cubeFragment.glsl` lights `baseColor.rgb` once; a
blend of two lit colours double-counts ambient.

## Triplanar projection

Vertical faces stretch under planar UVs. Sample the tile three times, in the `xy`, `yz`
and `zx` planes of the world position, and weight by the squared normal:

```
w = abs(n); w = pow(w, vec3(4.0)); w /= (w.x + w.y + w.z)
colour = tex(p.yz) * w.x + tex(p.zx) * w.y + tex(p.xy) * w.z
```

Dream's terrain is axis-aligned cubes, so a face's normal is one axis and two of the
three weights collapse to zero. Triplanar therefore buys nothing on cube faces and costs
three fetches: it is only worth it on the subdivided sculptures
(`SculptureSubdivisionResolution`), whose microcell faces do slope. Branch on whether the
mesh came from a subdivided cell rather than applying it everywhere.

## Stochastic tile offset

One 256 px tile across a 100-cell world repeats visibly. Break it per cell without any
new asset:

```
id    = floor(worldXZ / tileWorldSize)
off   = hash2(id)                 // 0..1, 0..1
angle = hash1(id) * 2π            // optional, quarter turns are cheaper and seam-safe
uv    = rotate(frac(worldXZ / tileWorldSize), angle) + off
```

Sampling two neighbouring variants and blending by the cell-local distance removes the
hard edge between offsets; one sample with quarter-turn-only rotation is cheaper and good
enough for the forge tiles, which are seamless on both axes. Derive the hash from the
integer tile id, never from the interpolated UV, or the offset swims as the camera moves.

Mipmaps: an offset or rotated lookup breaks the derivative chain. Sample with an explicit
LOD or keep the unmodified UV around for `textureGrad`, otherwise distant ground shimmers.
