"""The engine's actor transfer function, offline, plus a silhouette rasteriser over a generator's primitives.

    python tools/ui-assets/enginelook.py          # self-test: the stated formula against hand-worked numbers

Spec: docs/superpowers/specs/2026-11-04-models-and-rigs/00-acceptance.md, contract C4 and criterion M3.

Why this exists. A3 (and M3) measure the SPREAD of the actor's rendered value, and the engine's actor path is
strictly linear in albedo with no tonemap and no gamma encode, so what moves the spread is the distribution of
N.L over the figure's surface, not the texels. That distribution is a property of the geometry, so it can be
measured here, in the generator, without starting the game. This module is the measuring instrument; it is
deliberately a pure function of its inputs.

Contract C4, restated here identically to the spec and VERIFIED against the shaders. The citations are symbol
names, not line numbers, because line numbers rot (they already did once: the actor path moved to
dreamDirectShaded and every number here went stale). `python enginelook.py` re-reads the shaders and FAILS if
any line of this block stops matching them (check_contract_against_shaders), and so does
FireShadowSamplingMirrorTests / ShadowDisabledIsIdentityTests on the C# side:

    G    = ambient(n)*ambientScale + (directShaded(n,p) + points(n,p)) * 0.75   enemyFragment.glsl main
    out  = clamp(max(albedo*G, albedo*floor) * exposure + emissive, 0, 1)       enemyFragment.glsl main
    ambient(n) = mix(ambientGround, ambientSky, n.y*0.5 + 0.5)                  lighting.glsl dreamAmbient
    direct(n)  = max(dot(n,sunDir),0)*sunI*sunCol
               + max(dot(n,moonDir),0)*moonI*moonCol                            lighting.glsl dreamDirect
    directShaded(n,p) = max(dot(n,sunDir),0)*sunI*sunCol*sunMask*sunV
               + max(dot(n,moonDir),0)*moonI*moonCol*moonV                      clouds.glsl dreamDirectShaded
    points: s = d^2/r^2 ; f = 1.27 * clamp(1-s^2,0,1)^2 / (1 + 8*s)
            contribution = colour * f * (0.25 + 0.75*max(dot(n,l),0)) * lightV  pointLights.glsl dreamPointLights
    albedo = texel/255 * baseColorFactor        # the texture bytes ARE linear albedo

sunMask = 1 - cloudShadowStrength*cover(sun ray at the cloud plane); the shadow map's visibility v =
dreamSunVisibility(p,n) belongs to the DOMINANT light, the one the map was rendered from (dreamShadowMapIsSun):
sunV = v and moonV = 1 when the sun dominates, sunV = 1 and moonV = v when the moon does. lightV =
dreamFireVisibility, the fire atlas's visibility for that light. Each is 1.0 when nothing is modelled, and the
engine applies them to the dominant directional term and to each point light. direct(n) (dreamDirect) is
directShaded with sunMask = sunV = 1 and stays the contract of the callers that have no fragment position
(boundaryMist, warMist).

What this instrument does NOT model, stated plainly. It has no scene geometry and no cloud sheet, so it measures
the UNOCCLUDED case (sunMask = sunV = moonV = lightV = 1 unless a caller passes otherwise via Directional.shade
and PointLight.visibility). With no geometry moonV is 1, so the instrument is exact for the unoccluded case: the
A3/M3 figure is measured at night (night_env, hours 20:00-04:00), where the sun intensity is exactly 0 and so the
cloud mask multiplies nothing. Once figures shadow themselves the moon map can dim the moon term too, which, like
the fire, can only widen the measured spread. For the fire it is a LOWER BOUND: with real fire shadows the figure
self-shadows (an arm across the torso, a head over the shoulders), which can only widen the measured value spread,
never narrow it, so a figure that meets A3 here meets it in the game. At a DAY hour the engine would dim the sun by
the cloud shadow (coverage 0.30-0.45 all day) and by the sun map; night_env refuses day hours rather than quietly
disagreeing with the engine.

One deliberate deviation from the spec's prose: the spec writes the point window as (1-s)^2, the shader writes
clamp(1-s,0,1)^2. They differ only past the radius, where the shader is 0 and the prose is positive; the shader
is the engine, so the shader wins. Everything else is character for character.

The night constants are not pasted: they are recomputed here from the same C# constants (SkyModelKeyframes,
SkyModelSettings, FrameLighting.ReferenceNoon), so the derived moon intensity and actor floor are DERIVED rather
than copied, and the self-test checks them against the spec's stated values.

Units. Positions are in the engine's world units, the unit the point-light radii are in (brazier radius 10).
The engine has no metre: the spec's derived table reads a world unit as a metre, so METRE = 1.0 here, stated
once, and every distance argument is a world unit.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

F64 = np.float64

# --------------------------------------------------------------------------- engine constants (C#, VERIFIED)

NOON_SUN_ELEVATION_DEG = 60.0                       # SkyModelKeyframes.NoonSunElevationDegrees
REFERENCE_NOON_SUN = (-0.28571430, 0.42857143, -0.85714287)   # FrameLighting.ReferenceNoon.SunDirection.xyz
DAY_SUN_COLOUR = (1.00, 0.98, 0.94)                 # SkyModelKeyframes.DaySunColor
DAY_SUN_INTENSITY = 0.82
DAY_AMBIENT_SKY = (0.20, 0.22, 0.26)
NIGHT_AMBIENT_SKY = (0.016, 0.024, 0.048)
NIGHT_AMBIENT_GROUND = (0.006, 0.007, 0.013)
MOON_TINT = (0.60, 0.70, 1.00)
NIGHT_ACTOR_AMBIENT_SCALE = 1.5                     # SkyModelKeyframes.NightActorAmbientScale
DAY_ACTOR_FLOOR = 0.35                              # SkyModelKeyframes.DayActorFloor, reused by Night()
DEFAULT_NIGHT_BRIGHTNESS = 0.07                     # SkyModelSettings.DefaultNightBrightness
LIGHT_FADE_BELOW, LIGHT_FADE_ABOVE = -0.04, 0.06    # SkyModel.LightFade*
ACTOR_DIRECT_SCALE = 0.75                           # enemyFragment.glsl ActorDirectScale
LUMA = (0.2126, 0.7152, 0.0722)                     # SkyModelKeyframes.Luminance

BRAZIER_COLOUR = (1.0 * 3.0, 0.72 * 3.0, 0.46 * 3.0)   # PlacedLightIndex.BrazierColor
BRAZIER_RADIUS = 10.0                                  # PlacedLightIndex.BrazierRadius
TORCH_COLOUR_BASE = (1.0, 0.62, 0.32)                  # HeldLightCatalog.TorchColor, before the intensity
TORCH_INTENSITY = 2.1                                  # HeldLightCatalog.TorchIntensity
TORCH_RADIUS = 8.0                                     # HeldLightCatalog.TorchRadius

METRE = 1.0           # see the module docstring: a world unit, the unit the radii above live in
PLAYER_WORLD_HEIGHT = 1.2   # PlayerModelAssetDefinition normalises an actor to this many world units

# The fixed play camera, Client/Presentation/IsometricView: pitch 50, yaw 45, looking at the focus point.
ISO_PITCH_DEG, ISO_YAW_DEG = 50.0, 45.0


def _v(x) -> np.ndarray:
    return np.asarray(x, dtype=F64)


def _unit(x) -> np.ndarray:
    a = _v(x)
    return a / max(float(np.linalg.norm(a)), 1e-12)


def _smoothstep(e0: float, e1: float, x: float) -> float:
    t = min(max((x - e0) / (e1 - e0), 0.0), 1.0)
    return t * t * (3.0 - 2.0 * t)


# --------------------------------------------------------------------------- the environment


@dataclass(frozen=True)
class Directional:
    direction: tuple        # unit, towards the light (the shader dots the normal with it directly)
    colour: tuple           # already multiplied by its intensity
    shade: float = 1.0      # the multiplier on this light's term: sunMask*sunV for the sun, moonV (the map's visibility when the moon dominates, else 1.0) for the moon


@dataclass(frozen=True)
class PointLight:
    position: tuple         # world units
    radius: float
    colour: tuple           # already multiplied by its intensity
    visibility: float = 1.0  # dreamFireVisibility: 0 fully shadowed .. 1 lit; 1.0 when no fire casts or none is modelled


@dataclass(frozen=True)
class Env:
    ambient_sky: tuple
    ambient_ground: tuple
    ambient_scale: float
    floor: float
    exposure: float
    directionals: tuple = ()
    points: tuple = ()

    def with_points(self, *points: PointLight) -> "Env":
        return Env(self.ambient_sky, self.ambient_ground, self.ambient_scale, self.floor, self.exposure,
                   self.directionals, tuple(points))


def noon_sun_direction() -> np.ndarray:
    """SkyModelKeyframes.ComputeNoonSunDirection: the legacy azimuth lifted to 60 degrees."""
    legacy = _v(REFERENCE_NOON_SUN)
    azimuth = _unit((legacy[0], 0.0, legacy[2]))
    e = math.radians(NOON_SUN_ELEVATION_DEG)
    return _unit(azimuth * math.cos(e) + _v((0.0, 1.0, 0.0)) * math.sin(e))


def sun_direction_at(hour: float) -> np.ndarray:
    """SkyModel.SunDirectionAt: the noon sun swung about the east axis, noon at 12:00."""
    noon = noon_sun_direction()
    east = _unit((-noon[2], 0.0, noon[0]))
    phi = math.tau * ((hour / 24.0) % 1.0 - 0.5)
    return noon * math.cos(phi) - east * math.sin(phi)


def night_moon_intensity() -> float:
    """SkyModelKeyframes.ComputeNightMoonIntensity. DERIVED; the self-test checks it against the spec's 0.0660."""
    noon = noon_sun_direction()
    up_facing = _v(DAY_AMBIENT_SKY) + _v(DAY_SUN_COLOUR) * (noon[1] * DAY_SUN_INTENSITY)
    target = DEFAULT_NIGHT_BRIGHTNESS * float(_v(LUMA) @ up_facing)
    return (target - float(_v(LUMA) @ _v(NIGHT_AMBIENT_SKY))) / (noon[1] * float(_v(LUMA) @ _v(MOON_TINT)))


def night_env(hour: float = 22.0) -> Env:
    """The frame lighting an actor sees at `hour`, which must fall in the pure Night keyframe band (20:00-04:00;
    SkyModelKeyframes.BuildTable has Night(20) and Night(0) with no twilight between). The sun is below the
    horizon there, so its intensity fades to 0 and only the moon and the ambient remain."""
    if not (hour >= 20.0 or hour <= 4.0):
        raise ValueError(f"hour {hour} is not inside the Night keyframe band (20:00-04:00)")
    sun = sun_direction_at(hour)
    moon = -sun
    moon_i = night_moon_intensity() * _smoothstep(LIGHT_FADE_BELOW, LIGHT_FADE_ABOVE, float(moon[1]))
    dirs = (Directional(tuple(moon), tuple(_v(MOON_TINT) * moon_i)),)
    return Env(NIGHT_AMBIENT_SKY, NIGHT_AMBIENT_GROUND, NIGHT_ACTOR_AMBIENT_SCALE,
               DAY_ACTOR_FLOOR * DEFAULT_NIGHT_BRIGHTNESS, 1.0, dirs, ())


def sun_shade(cloud_cover: float = 0.0, cloud_strength: float = 0.0, visibility: float = 1.0) -> float:
    """clouds.glsl dreamDirectShaded's multiplier on the sun term: sunMask * sunV, with
    sunMask = 1 - cloudShadowStrength * cover (the cover where the sun ray crosses the cloud plane)."""
    return (1.0 - cloud_strength * cloud_cover) * visibility


def brazier(position) -> PointLight:
    return PointLight(tuple(_v(position)), BRAZIER_RADIUS, BRAZIER_COLOUR)


def torch(position, intensity: float = TORCH_INTENSITY) -> PointLight:
    return PointLight(tuple(_v(position)), TORCH_RADIUS, tuple(_v(TORCH_COLOUR_BASE) * intensity))


# --------------------------------------------------------------------------- the transfer function (C4)


def ambient_term(n: np.ndarray, env: Env) -> np.ndarray:
    t = (n[:, 1:2] * 0.5 + 0.5)
    return _v(env.ambient_ground)[None, :] * (1.0 - t) + _v(env.ambient_sky)[None, :] * t


def direct_term(n: np.ndarray, env: Env) -> np.ndarray:
    out = np.zeros((len(n), 3), dtype=F64)
    for d in env.directionals:
        lam = np.maximum(n @ _unit(d.direction), 0.0)
        out += lam[:, None] * (_v(d.colour) * d.shade)[None, :]
    return out


POINT_PEAK = 1.27     # pointLights.glsl PointPeak: the floor-disc integral of the window equals the old (1-s)^2/(1+1.5s)'s
POINT_CORE = 8.0      # pointLights.glsl PointCore


def point_term(n: np.ndarray, p: np.ndarray, env: Env) -> np.ndarray:
    out = np.zeros((len(n), 3), dtype=F64)
    for L in env.points:
        to = _v(L.position)[None, :] - p
        d2 = (to * to).sum(1)
        s = d2 / (L.radius * L.radius)
        w = np.clip(1.0 - s * s, 0.0, 1.0)
        f = POINT_PEAK * w * w / (1.0 + POINT_CORE * s)
        lam = np.maximum((n * (to / np.sqrt(np.maximum(d2, 1e-4))[:, None])).sum(1), 0.0)
        out += _v(L.colour)[None, :] * (f * (0.25 + 0.75 * lam) * L.visibility)[:, None]
    return out


def gain(n: np.ndarray, p: np.ndarray, env: Env) -> np.ndarray:
    """G of C4: the multiplier the shader puts on the albedo, per fragment, before the floor and the clamp."""
    return (ambient_term(n, env) * env.ambient_scale
            + (direct_term(n, env) + point_term(n, p, env)) * ACTOR_DIRECT_SCALE)   # direct_term is dreamDirectShaded


def actor_rgb(albedo: np.ndarray, n: np.ndarray, p: np.ndarray, env: Env, emissive=None) -> np.ndarray:
    """C4 end to end. albedo, n, p are (N,3); emissive is (N,3) or None. Returns the clamped linear RGB the
    engine writes to the framebuffer - with no tonemap and no gamma encode, that byte IS this value."""
    a = _v(albedo)
    lit = np.maximum(a * gain(n, p, env), a * env.floor) * env.exposure
    if emissive is not None:
        lit = lit + _v(emissive)
    return np.clip(lit, 0.0, 1.0)


def value(rgb: np.ndarray) -> np.ndarray:
    """HSV value, the quantity check_look.py's A3 takes percentiles of: max(r,g,b)."""
    return _v(rgb).max(axis=1)


# --------------------------------------------------------------------------- the silhouette rasteriser


def camera_basis(pitch_deg: float = ISO_PITCH_DEG, yaw_deg: float = ISO_YAW_DEG):
    """(right, up, towards_camera) for the fixed play camera. The play camera is perspective at 18 units with a
    35 degree field of view; this projects orthographically along the same axis, which at that distance moves a
    figure's silhouette by well under a pixel and keeps the measurement independent of the framing."""
    pitch, yaw = math.radians(pitch_deg), math.radians(yaw_deg)
    w = _unit((math.cos(pitch) * math.cos(yaw), math.sin(pitch), math.cos(pitch) * math.sin(yaw)))
    right = _unit(np.cross(_v((0.0, 1.0, 0.0)), w))
    return right, _unit(np.cross(w, right)), w


@dataclass
class Surface:
    """One drawable batch in world units, glTF axes (Y up): triangle soup plus what shading needs."""
    positions: np.ndarray    # (V,3)
    normals: np.ndarray      # (V,3)
    albedo: np.ndarray       # (V,3) linear albedo per vertex
    indices: np.ndarray      # (T,3)
    emissive: tuple | None = None    # linear emissive colour of the whole batch, or None


def _texel_albedo(uvs: np.ndarray, texture: np.ndarray, factor) -> np.ndarray:
    """Nearest-texel lookup. The engine filters linearly, but a texel is about a pixel at this framing and the
    percentiles of a 50k-pixel silhouette do not move on the difference (checked: under 0.002)."""
    h, w = texture.shape[:2]
    u = np.clip((np.asarray(uvs, dtype=F64)[:, 0] * w).astype(np.int64), 0, w - 1)
    v = np.clip((np.asarray(uvs, dtype=F64)[:, 1] * h).astype(np.int64), 0, h - 1)
    return texture[v, u, :3].astype(F64) / 255.0 * _v(factor)[None, :3]


def rasterise(surfaces, env: Env, height_px: int = 256, pad_px: int = 6,
              basis=None) -> dict:
    """Shade `surfaces` with C4 through an orthographic iso camera and report the silhouette's value statistics.

    Returns p10 / p50 / p90 / spread of the HSV value over the covered pixels - the same statistic
    check_look.py's A3 takes from a real capture - plus the coverage, the lambert histogram that drives it and
    the value image itself (for an eyeball or a preview)."""
    right, up, toward = basis if basis is not None else camera_basis()
    P = np.vstack([s.positions for s in surfaces])
    sx, sy = P @ right, P @ up
    lo_x, hi_x, lo_y, hi_y = sx.min(), sx.max(), sy.min(), sy.max()
    span = max(hi_y - lo_y, 1e-6)
    scale = (height_px - 2 * pad_px) / span
    width_px = int(math.ceil((hi_x - lo_x) * scale)) + 2 * pad_px
    depth = np.full((height_px, width_px), -np.inf)
    val = np.zeros((height_px, width_px))
    lam = np.full((height_px, width_px), np.nan)
    cov = np.zeros((height_px, width_px), dtype=bool)
    ids = np.full((height_px, width_px), -1, dtype=np.int64)

    for si, s in enumerate(surfaces):
        v = np.asarray(s.positions, dtype=F64)
        X = (v @ right - lo_x) * scale + pad_px
        Y = (height_px - pad_px) - (v @ up - lo_y) * scale
        Z = v @ toward
        tris = np.asarray(s.indices, dtype=np.int64).reshape(-1, 3)
        for t in tris:
            x0, x1, x2 = X[t]
            y0, y1, y2 = Y[t]
            den = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2)
            if abs(den) < 1e-12:
                continue
            gx = np.arange(max(int(math.floor(min(x0, x1, x2))), 0),
                           min(int(math.ceil(max(x0, x1, x2))) + 1, width_px))
            gy = np.arange(max(int(math.floor(min(y0, y1, y2))), 0),
                           min(int(math.ceil(max(y0, y1, y2))) + 1, height_px))
            if not len(gx) or not len(gy):
                continue
            GX, GY = np.meshgrid(gx + 0.5, gy + 0.5)
            l0 = ((y1 - y2) * (GX - x2) + (x2 - x1) * (GY - y2)) / den
            l1 = ((y2 - y0) * (GX - x2) + (x0 - x2) * (GY - y2)) / den
            l2 = 1.0 - l0 - l1
            ok = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
            if not ok.any():
                continue
            py, px = GY[ok].astype(np.int64), GX[ok].astype(np.int64)
            b = np.stack((l0[ok], l1[ok], l2[ok]), 1)
            z = b @ Z[t]
            front = z > depth[py, px]
            if not front.any():
                continue
            py, px, b = py[front], px[front], b[front]
            pos = b @ v[t]
            nrm = b @ np.asarray(s.normals, dtype=F64)[t]
            nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-12)
            alb = b @ np.asarray(s.albedo, dtype=F64)[t]
            emi = None if s.emissive is None else np.tile(_v(s.emissive), (len(pos), 1))
            rgb = actor_rgb(alb, nrm, pos, env, emi)
            depth[py, px] = b @ Z[t]
            val[py, px] = value(rgb)
            cov[py, px] = True
            ids[py, px] = si
            if env.points:
                L = env.points[0]
                to = _v(L.position)[None, :] - pos
                to /= np.maximum(np.linalg.norm(to, axis=1, keepdims=True), 1e-12)
                lam[py, px] = np.maximum((nrm * to).sum(1), 0.0)

    av = val[cov]
    if not len(av):
        raise ValueError("nothing was rasterised")
    p10, p50, p90 = (float(np.percentile(av, q)) for q in (10, 50, 90))
    live = lam[cov & np.isfinite(lam)]
    hist = np.histogram(live, bins=10, range=(0.0, 1.0))[0] / max(len(live), 1) if len(live) else None
    return dict(p10=p10, p50=p50, p90=p90, spread=p90 - p10, mean=float(av.mean()),
                pixels=int(cov.sum()), width_px=width_px, height_px=height_px,
                lambert_hist=None if hist is None else [round(float(x), 4) for x in hist],
                lambert_mid_share=None if hist is None else float(hist[3:7].sum()),
                bright_share=float((av > 0.60).mean()),   # check_look.py's BRIGHT_V, over the figure alone
                black_share=float((av < 0.10).mean()),    # its BLACK_V
                value=val, covered=cov, surface=ids)


# --------------------------------------------------------------------------- assets -> surfaces


def _quat_matrix(q) -> np.ndarray:
    x, y, z, w = (float(c) for c in q)
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]], dtype=F64)


def node_worlds(nodes, clip=None, key: int = 0) -> dict:
    """Forward kinematics over a node hierarchy, bind pose or one key of one clip.

    An animation channel REPLACES the node's own TRS; it is not a delta on top of it. That distinction is
    invisible on the player, whose root node sits at translation (0, 0, 0) so the two readings coincide, and it
    is a 1.02-unit error on the hound, whose root carries the hip height. MEASURED: reading it as a delta put
    every frame of the hound's `walk` and `die` exactly one root offset away from the CPU skinning they were
    authored from."""
    rot = {i: np.array([0.0, 0.0, 0.0, 1.0]) for i in range(len(nodes))}
    trs = {i: _v(n.translation) for i, n in enumerate(nodes)}
    if clip is not None:
        for i, (_, q) in clip.rotations.items():
            rot[i] = _v(q[key])
        for i, (_, t) in clip.translations.items():
            trs[i] = _v(t[key])
    world = {}
    for i, n in enumerate(nodes):
        M = np.eye(4)
        M[:3, :3] = _quat_matrix(rot[i])
        M[:3, 3] = trs[i]
        world[i] = M if n.parent is None else world[n.parent] @ M
    return world


def asset_surfaces(asset: dict, textures, clip=None, key: int = 0,
                   world_height: float = PLAYER_WORLD_HEIGHT, ground: tuple = (0.0, 0.0, 0.0)):
    """A generator's build_asset() output -> Surfaces in world units, standing with its feet at `ground`.

    `textures` is one decoded RGBA array per material (the generator's "encoded" atlas) or None for a material
    with no texture, in which case the baseColorFactor alone is the albedo.

    A primitive with `node = None` is a SKINNED mesh: gltfwrite hangs it off its own extra root node, whose own
    transform the engine cancels out (ComposeSkinJointMatrix divides by meshWorld), so its vertices are already
    in model space and take no node transform here. That is the bind pose, which is what M3 measures - the
    deformation is checked separately, against CPU skinning."""
    nodes, prims, mats = asset["nodes"], asset["primitives"], asset["materials"]
    world = node_worlds(nodes, clip, key)
    raw, out = [], []
    for p in prims:
        M = np.eye(4) if p.node is None else world[p.node]
        v = np.asarray(p.positions, dtype=F64) @ M[:3, :3].T + M[:3, 3]
        n = np.asarray(p.normals, dtype=F64) @ M[:3, :3].T
        raw.append((p, v, n))
    ymin = min(float(v[:, 1].min()) for _, v, _ in raw)
    ymax = max(float(v[:, 1].max()) for _, v, _ in raw)
    k = world_height / max(ymax - ymin, 1e-9)
    base = _v(ground) - _v((0.0, ymin * k, 0.0))
    for p, v, n in raw:
        m = mats[p.material]
        tex = textures[p.material] if textures is not None else None
        factor = m.get("base_color_factor", (1, 1, 1, 1))
        alb = (_texel_albedo(p.uvs, tex, factor) if tex is not None
               else np.tile(_v(factor[:3]), (len(v), 1)))
        n = n / np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
        out.append(Surface(v * k + base, n, alb, np.asarray(p.indices, dtype=np.int64).reshape(-1, 3),
                           m.get("emissive_factor")))
    return out


M3_DISTANCE = 1.5 * METRE          # the spec's "a brazier 1.5 m away"
M3_AZIMUTH_DEG = 35.0              # the key light's angle in the screen plane, measured from screen right
M3_TOWARD = 0.5                    # how much of the light's offset is towards the camera
M3_AZIMUTHS = (35.0, 145.0)        # the key from the upper right and its mirror; M3 gates on the WORSE of the two


def m3_light(distance: float = M3_DISTANCE, azimuth_deg: float = M3_AZIMUTH_DEG, toward: float = M3_TOWARD,
             figure_height: float = PLAYER_WORLD_HEIGHT) -> PointLight:
    """M3's brazier, placed in the CAMERA's frame rather than the figure's.

    Why the camera's frame. In play the figure turns and the brazier does not, so a light fixed to the figure's
    own front measures that one bearing only. What the measurement is after is whether the geometry splits the
    light into lit facets and unlit ones, and that is a screen-space question: a facet contributes a bright
    pixel only if it faces both the camera and the light. `azimuth_deg` is the light's angle in the screen plane
    from screen right (so 35 degrees is up and to the right, a key light), `toward` how far it also sits in
    front of the figure towards the camera. A light straight behind the camera (toward -> large) flattens every
    figure equally and the gate would stop measuring geometry; 0.5 keeps it raking."""
    right, up, tow = camera_basis()
    a = math.radians(azimuth_deg)
    d = _unit(right * math.cos(a) + up * math.sin(a) + tow * toward)
    centre = _v((0.0, figure_height * 0.55, 0.0))
    return brazier(centre + d * distance)


def m3_env(distance: float = M3_DISTANCE, azimuth_deg: float = M3_AZIMUTH_DEG, hour: float = 22.0,
           toward: float = M3_TOWARD, figure_height: float = PLAYER_WORLD_HEIGHT) -> Env:
    """Night at `hour` plus M3's brazier. Nothing else: no second light, no bounce, no shadowing. The engine DOES
    shadow the fire now (fireShadows.glsl), but only by occlusion from other geometry, which this instrument
    does not have; unoccluded, a dark pixel on an actor is a pixel whose normal is turned away, which is exactly
    the property the geometry controls, and self-shadowing can only add more dark (see the module docstring)."""
    return night_env(hour).with_points(m3_light(distance, azimuth_deg, toward, figure_height))


def m3_measure(surfaces, azimuths=M3_AZIMUTHS, height_px: int = 256, **kw) -> dict:
    """M3 over every azimuth in `azimuths`. `spread` is the WORST of them: a figure must not pass by being
    plated on one side only. Per-azimuth numbers come back under `per_azimuth`."""
    per = {}
    for a in azimuths:
        r = rasterise(surfaces, m3_env(azimuth_deg=a, **kw), height_px=height_px)
        per[a] = {k: v for k, v in r.items() if k not in ("value", "covered")}
    worst = min(per, key=lambda a: per[a]["spread"])
    return dict(spread=per[worst]["spread"], worst_azimuth=worst, per_azimuth=per)


# --------------------------------------------------------------------------- the contract check

# Every line the header restates, with the shader it must appear in. Each is checked three ways by
# check_contract_against_shaders: it is in the shader, it is in this module's docstring (with the shader's own
# spelling of the expression), so the header, this table and the shaders cannot drift apart silently.
CONTRACT = (
    ("enemyFragment.glsl",
     "vec3 lit = (dreamAmbient(n) * uLightingParams.z + (dreamDirectShaded(n, fragPos) + dreamPointLights(n, fragPos)) "
     "* ActorDirectScale) * baseColor.rgb;"),
    ("enemyFragment.glsl", "lit = max(lit, baseColor.rgb * uLightingParams.y);"),
    ("enemyFragment.glsl", "const float ActorDirectScale = 0.75;"),
    ("lighting.glsl", "vec3 dreamAmbient(vec3 n) { return mix(uAmbientGround.rgb, uAmbientSky.rgb, n.y * 0.5 + 0.5); }"),
    ("lighting.glsl", "return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb"),
    ("lighting.glsl", "+ max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;"),
    ("clouds.glsl",
     "return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb * sunMask * (sunMap ? v : 1.0)"),
    ("clouds.glsl", "float v = dreamSunVisibility(fragPos, n);"),
    ("clouds.glsl", "bool sunMap = dreamShadowMapIsSun();"),
    ("clouds.glsl",
     "+ max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb * (sunMap ? 1.0 : v);"),
    ("clouds.glsl", "sunMask = 1.0 - uCloudParams.z * dreamCloudCover(onPlane.xz);"),
    ("pointLights.glsl", "float s = d2 / (r * r);"),
    ("pointLights.glsl", "float f = dreamPointWindow(s);"),
    ("pointLights.glsl", "float w = clamp(1.0 - s * s, 0.0, 1.0);"),
    ("pointLights.glsl", "return PointPeak * w * w / (1.0 + PointCore * s);"),
    ("pointLights.glsl", "const float PointPeak = 1.27;"),
    ("pointLights.glsl", "const float PointCore = 8.0;"),
    ("pointLights.glsl", "sum += uPointLightColor[i].rgb * (f * (0.25 + 0.75 * lambert)) * visibility;"),
)

# The header's own spelling of the same contract, as it appears in the docstring above (math notation, not GLSL).
HEADER_FRAGMENTS = (
    "G    = ambient(n)*ambientScale + (directShaded(n,p) + points(n,p)) * 0.75",
    "out  = clamp(max(albedo*G, albedo*floor) * exposure + emissive, 0, 1)",
    "ambient(n) = mix(ambientGround, ambientSky, n.y*0.5 + 0.5)",
    "directShaded(n,p) = max(dot(n,sunDir),0)*sunI*sunCol*sunMask*sunV",
    "+ max(dot(n,moonDir),0)*moonI*moonCol*moonV",
    "s = d^2/r^2 ; f = 1.27 * clamp(1-s^2,0,1)^2 / (1 + 8*s)",
    "contribution = colour * f * (0.25 + 0.75*max(dot(n,l),0)) * lightV",
)


def _shader_dir():
    import pathlib
    return pathlib.Path(__file__).resolve().parents[2] / "dream" / "shaders"


def check_contract_against_shaders(shader_dir=None, doc: str | None = None) -> int:
    """FAILS (AssertionError, listing every divergence) when the shaders and this header disagree. Returns the
    number of contract lines checked. `shader_dir` and `doc` are injectable so the self-test can prove it fails."""
    root = shader_dir if shader_dir is not None else _shader_dir()
    doc = __doc__ if doc is None else doc
    problems = []
    cache = {}

    def source(name):
        if name not in cache:
            cache[name] = " ".join((root / name).read_text(encoding="utf-8").replace("\r", "").split())
        return cache[name]

    for name, line in CONTRACT:
        if " ".join(line.split()) not in source(name):
            problems.append(f"{name}: the contract line is no longer in the shader: {line}")
    for fragment in HEADER_FRAGMENTS:
        if " ".join(fragment.split()) not in " ".join(doc.split()):
            problems.append(f"the header no longer states: {fragment}")

    # The constants this module restates by value.
    scale = float(next(l for n, l in CONTRACT if "ActorDirectScale = " in l).split("=")[1].strip(" ;"))
    if scale != ACTOR_DIRECT_SCALE:
        problems.append(f"ACTOR_DIRECT_SCALE {ACTOR_DIRECT_SCALE} != the shader's {scale}")
    # The moon term carries the map visibility when the moon dominates: exactly one moon term, with the (sunMap ? 1.0 : v)
    # select and no bare moon term left, and the sun term carries the opposite select.
    shaded = source("clouds.glsl")
    bare_moon = "uMoonColor.rgb;"
    moon_term = "uMoonColor.rgb * (sunMap ? 1.0 : v);"
    if shaded.count(moon_term) != 1 or shaded.count(bare_moon) != 0:
        problems.append("clouds.glsl: the moon term of dreamDirectShaded no longer carries the map visibility (dreamSunVisibility)")
    if shaded.count("sunMask * (sunMap ? v : 1.0)") != 1:
        problems.append("clouds.glsl: the sun term of dreamDirectShaded no longer carries the map visibility (dreamSunVisibility)")
    if problems:
        raise AssertionError("enginelook.py has diverged from the shaders:\n  " + "\n  ".join(problems))
    return len(CONTRACT) + len(HEADER_FRAGMENTS)


# --------------------------------------------------------------------------- self-test


def _selftest() -> None:
    env = night_env(22.0)
    moon = env.directionals[0]
    assert abs(night_moon_intensity() - 0.0660) < 5e-4, night_moon_intensity()
    assert abs(env.floor - 0.0245) < 1e-9, env.floor
    assert env.ambient_scale == 1.5 and env.exposure == 1.0
    print(f"night 22:00  moon dir {tuple(round(float(x), 4) for x in moon.direction)} "
          f"intensity {night_moon_intensity():.4f} (spec 0.0660)  floor {env.floor:.4f} (spec 0.0245)")

    # ambient endpoints: a straight-up normal gets the sky, a straight-down one the ground
    n = np.array([[0.0, 1.0, 0.0], [0.0, -1.0, 0.0], [1.0, 0.0, 0.0]])
    amb = ambient_term(n, env)
    assert np.allclose(amb[0], NIGHT_AMBIENT_SKY) and np.allclose(amb[1], NIGHT_AMBIENT_GROUND)
    assert np.allclose(amb[2], 0.5 * (_v(NIGHT_AMBIENT_SKY) + _v(NIGHT_AMBIENT_GROUND)))

    # the point window: 1 at the centre, 0 at and past the radius, and the hand-worked value at d = 1.5
    p = np.zeros((1, 3))
    for d in (0.5, 2.0, BRAZIER_RADIUS, BRAZIER_RADIUS * 2):
        sd = (d / BRAZIER_RADIUS) ** 2
        want = POINT_PEAK * max(1.0 - sd * sd, 0.0) ** 2 / (1.0 + POINT_CORE * sd)   # lambert 1: the whole window, no wrap term
        e = env.with_points(PointLight((0.0, 0.0, d), BRAZIER_RADIUS, (1.0, 1.0, 1.0)))
        got = float(point_term(np.array([[0.0, 0.0, 1.0]]), p, e)[0, 0])
        assert abs(got - want) < 1e-9, (d, got, want)
        assert (d < BRAZIER_RADIUS) == (want > 0.0)
    s = (1.5 / BRAZIER_RADIUS) ** 2
    f = POINT_PEAK * (1 - s * s) ** 2 / (1 + POINT_CORE * s)
    e = env.with_points(brazier((0.0, 0.0, 1.5)))
    facing = float(point_term(np.array([[0.0, 0.0, 1.0]]), p, e)[0, 0])
    away = float(point_term(np.array([[0.0, 0.0, -1.0]]), p, e)[0, 0])
    assert abs(facing - BRAZIER_COLOUR[0] * f) < 1e-9
    assert abs(away - BRAZIER_COLOUR[0] * f * 0.25) < 1e-9
    print(f"brazier at 1.5 units: window f {f:.4f}, red gain facing {facing * ACTOR_DIRECT_SCALE:.4f} "
          f"vs away {away * ACTOR_DIRECT_SCALE:.4f} -> {facing / away:.2f}x on N.L alone")

    # the floor really is a floor, and emissive bypasses nothing but the gain
    dark = Env((0.0,) * 3, (0.0,) * 3, 1.0, 0.0245, 1.0)
    a = np.array([[0.4, 0.4, 0.4]])
    assert np.allclose(actor_rgb(a, n[:1], p, dark), a * 0.0245)
    assert np.allclose(actor_rgb(a, n[:1], p, dark, np.array([[0.1, 0.0, 0.0]])),
                       a * 0.0245 + np.array([[0.1, 0.0, 0.0]]))
    toward_light = np.array([[0.0, 0.0, 1.0]])
    assert float(gain(toward_light, p, e)[0, 0]) > 2.0, "the brazier at 1.5 units should overdrive a pale texel"
    assert float(actor_rgb(np.array([[1.0, 1.0, 1.0]]), toward_light, p, e)[0, 0]) == 1.0, "no clamp at 1"
    print("transfer function: ambient endpoints, point window, floor, emissive and the clamp all exact")

    # the rasteriser on two plates, one facing the light and one away: a bimodal value histogram by construction
    def plate(c, nrm):
        q = np.array([[-0.3, 0.0, 0.0], [0.3, 0.0, 0.0], [0.3, 0.6, 0.0], [-0.3, 0.6, 0.0]]) + _v(c)
        return Surface(q, np.tile(_unit(nrm), (4, 1)), np.tile(np.array([0.5, 0.5, 0.5]), (4, 1)),
                       np.array([[0, 1, 2], [0, 2, 3]]))
    right, up, toward = camera_basis()
    lit = m3_env()
    one = rasterise([plate((-0.35, 0.0, 0.0), right + toward)], lit)
    two = rasterise([plate((-0.35, 0.0, 0.0), right + toward), plate((0.35, 0.0, 0.0), -right + toward)], lit)
    assert two["spread"] > one["spread"] * 3, (one["spread"], two["spread"])
    print(f"rasteriser: one lit plate spreads {one['spread']:.4f}; adding a plate turned away from the light "
          f"spreads {two['spread']:.4f} over {two['pixels']} px")
    again = rasterise([plate((-0.35, 0.0, 0.0), right + toward), plate((0.35, 0.0, 0.0), -right + toward)], lit)
    assert again["spread"] == two["spread"], "rasteriser is not deterministic"

    # the visibility multiplicands: a point light's term scales EXACTLY by its visibility, and the sun's by its shade;
    # the dominant light's term carries the map visibility: sunMask*sunV on the sun, moonV on the moon
    pt = np.zeros((1, 3))
    up = np.array([[0.0, 1.0, 0.0]])
    lit_light = PointLight((0.0, 1.0, 2.0), BRAZIER_RADIUS, (1.0, 0.7, 0.4))
    shadowed = PointLight(lit_light.position, lit_light.radius, lit_light.colour, 0.25)
    base = point_term(up, pt, Env((0,) * 3, (0,) * 3, 1.0, 0.0, 1.0, (), (lit_light,)))
    quarter = point_term(up, pt, Env((0,) * 3, (0,) * 3, 1.0, 0.0, 1.0, (), (shadowed,)))
    assert np.allclose(quarter, base * 0.25) and float(base[0, 0]) > 0.0
    dark = point_term(up, pt, Env((0,) * 3, (0,) * 3, 1.0, 0.0, 1.0, (), (PointLight(lit_light.position, lit_light.radius, lit_light.colour, 0.0),)))
    assert np.allclose(dark, 0.0), "a fully shadowed light must contribute nothing"
    sun = Directional((0.0, 1.0, 0.0), (1.0, 1.0, 1.0), sun_shade(cloud_cover=0.5, cloud_strength=0.6, visibility=0.5))
    moon = Directional((0.0, 1.0, 0.0), (0.2, 0.3, 0.5))
    assert abs(sun.shade - 0.7 * 0.5) < 1e-12
    both = direct_term(up, Env((0,) * 3, (0,) * 3, 1.0, 0.0, 1.0, (sun, moon), ()))
    assert np.allclose(both[0], np.array([1.0, 1.0, 1.0]) * 0.35 + np.array([0.2, 0.3, 0.5])), both
    assert np.allclose(direct_term(up, Env((0,) * 3, (0,) * 3, 1.0, 0.0, 1.0, (Directional(sun.direction, sun.colour),), ())),
                       [[1.0, 1.0, 1.0]]), "shade defaults to 1.0: the engine contract"
    print("visibility: point lights scale exactly by lightV, the sun by sunMask*sunV, the moon by moonV")

    # the header, this module and the shaders agree - and the check really fails when they do not
    checked = check_contract_against_shaders()
    print(f"contract: {checked} lines match the shaders in {_shader_dir()}")
    import pathlib
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        tmp = pathlib.Path(tmp)
        for name in {n for n, _ in CONTRACT}:
            text = (_shader_dir() / name).read_text(encoding="utf-8")
            if name == "clouds.glsl":
                text = text.replace("* sunMask * (sunMap ? v : 1.0)", "* sunMask")
            (tmp / name).write_text(text, encoding="utf-8")
        try:
            check_contract_against_shaders(tmp)
        except AssertionError as e:
            assert "dreamSunVisibility" in str(e), e
        else:
            raise SystemExit("the contract check did not fail on a shader that dropped the sun visibility")
        (tmp / "clouds.glsl").write_text(
            (_shader_dir() / "clouds.glsl").read_text(encoding="utf-8").replace(" * (sunMap ? 1.0 : v);", ";"), encoding="utf-8")
        try:
            check_contract_against_shaders(tmp)
        except AssertionError as e:
            assert "moon term" in str(e), e
        else:
            raise SystemExit("the contract check did not fail on a shader that dropped the moon visibility")
        try:
            check_contract_against_shaders(doc=__doc__.replace("sunMask*sunV", "sunMask"))
        except AssertionError:
            pass
        else:
            raise SystemExit("the contract check did not fail on a header that dropped the sun visibility")
    print("contract: the check fails when a shader or the header loses a term")
    print("PASS enginelook self-test")


if __name__ == "__main__":
    _selftest()
