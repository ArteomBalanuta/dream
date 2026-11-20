"""Gates for the forged actors that are not the player: the three villagers and the skinned hound.

    python tools/ui-assets/tests/check_actors.py

Fails (exit 1) when any gate fails. check_char.py keeps the player; this keeps everything else, and the two
share their arithmetic through enginelook.py so a gate and the measuring instrument cannot drift apart.

House rule, and the reason this file is as long as it is: EVERY gate is run a second time on an input that must
FAIL it. A gate that has silently stopped measuring looks exactly like a gate that passes, and the A3 criterion
was caught being wrong twice that way. The siblings here are not synthetic noise - each one is the specific
mistake its gate exists to catch: an ungrounded skeleton, an unscaled inverse bind, a figure with its plates
taken away, a weight row that does not sum to 1.
"""
from __future__ import annotations

import copy
import dataclasses
import hashlib
import math
import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent
if str(TOOLS) not in sys.path:
    sys.path.insert(0, str(TOOLS))

import charforge as C  # noqa: E402
import enginelook as L  # noqa: E402
import gen_char_beast as B  # noqa: E402
import gen_char_npc as N  # noqa: E402
import gen_char_player as P  # noqa: E402
import gen_char_skeleton as K  # noqa: E402
import gltfwrite as G  # noqa: E402

M3_SPREAD_MIN = 0.50          # the same floor check_char.py holds the player to (spec M3)
MAX_JOINTS = 32               # contract C3
FK_TOLERANCE = 1e-5           # CPU skinning vs glTF forward kinematics, in world units
WEIGHT_SUM_TOL = 1e-6         # a WEIGHTS_0 row must sum to 1 to within this
# The share of vertices that take a second joint's influence. A rigid-only bind - every vertex welded to one
# joint, which is what a skinning step that lost its blend would produce - reads exactly 0, and it used to be
# printed with no comparison at all. Measured 72.8% today; the floor is set well under a third of that so
# ordinary authoring (fewer blended seams, a stiffer tail) moves freely, while the failure the measurement was
# written to describe collapses to 0 and cannot sit above it.
BLENDED_MIN = 0.20
BYTE_CEILING = {"villager-vendor": 320_000, "villager-smith": 320_000, "villager-warden": 320_000,
                "hound": 420_000, "skeleton": 900_000}
# R9: dream/animations/props drifted to 13 MB with dead_oak.glb alone at 3.3 MB. These ceilings are each about
# 1.6x the asset's measured size, so ordinary authoring moves freely and a tessellation or atlas mistake does not.
ASSETS = TOOLS.parents[1] / "dream" / "animations"
# The engine's own copies, the ones the catalogs load. The generators must reproduce them byte for byte.
SHIPPED = {"villager-vendor": ASSETS / "npcs" / "villager-vendor.glb",
           "villager-smith": ASSETS / "npcs" / "villager-smith.glb",
           "villager-warden": ASSETS / "npcs" / "villager-warden.glb",
           "hound": ASSETS / "enemies" / "hound.glb",
           "skeleton": ASSETS / "enemies" / "skeleton.glb"}
HOUND_WORLD_HEIGHT = 0.60     # WolfAssetDefinition.Normalization.TargetWorldHeight; GraveHowler shares it
VILLAGER_WORLD_HEIGHT = L.PLAYER_WORLD_HEIGHT   # the spec's decision: villagers match the player

errs: list[str] = []


def fail(msg: str) -> None:
    errs.append(msg)
    print(f"  FAIL {msg}")


def ok(msg: str) -> None:
    print(f"  ok   {msg}")


# ---------------------------------------------------------------------------- shared measurements
# Each of these is called by its gate AND by that gate's sibling. A sibling that re-implements the comparison, or
# that derives its expected value from the thing being checked, proves nothing: the two dead controls this file
# used to carry were both that shape.


def weight_sums(primitives) -> np.ndarray:
    """Every WEIGHTS_0 row's sum, over every primitive."""
    return np.concatenate([np.asarray(p.weights, dtype=np.float64).sum(1) for p in primitives])


def blended_share(primitives) -> float:
    """Share of vertices whose second joint carries more than a rounding weight, averaged over primitives."""
    return float(np.mean([(np.asarray(p.weights)[:, 1] > 0.01).mean() for p in primitives]))


def renderable_errors(label: str, doc: dict) -> list[str]:
    """Contract C1: the renderable nodes of an exported villager are EXACTLY N.RENDERABLE, by set equality."""
    renderable = {n["name"] for n in doc["nodes"] if "mesh" in n}
    if renderable != set(N.RENDERABLE):
        return [f"{label}: renderable nodes {sorted(renderable)} are not contract C1's {sorted(N.RENDERABLE)}"]
    return []


# ---------------------------------------------------------------------------- shared gates

def gate_determinism(label: str, build, to_bytes) -> dict:
    a, b = build(), build()
    ha, hb = hashlib.sha1(to_bytes(a)).hexdigest(), hashlib.sha1(to_bytes(b)).hexdigest()
    if ha != hb:
        fail(f"{label}: two builds produced different GLB bytes")
    else:
        ok(f"{label}: deterministic, sha1 {ha[:12]} ({len(to_bytes(a))} bytes)")
    return a


def gate_shipped(label: str, data: bytes) -> None:
    """The generated bytes must BE the bytes the engine loads.

    The determinism gate above builds twice and compares the two builds to each other, which says nothing
    about the file under dream/animations: editing a generator without re-running it left every shipped asset
    stale with every gate green. They match today, which is exactly when to close it."""
    shipped = SHIPPED[label]
    if not shipped.exists():
        fail(f"{label}: {shipped} is not shipped")
        return
    on_disk = shipped.read_bytes()
    if on_disk != data:
        fail(f"{label}: the generator produces {len(data)} bytes, sha1 {hashlib.sha1(data).hexdigest()[:12]}, but "
             f"{shipped.name} on disk is {len(on_disk)} bytes, sha1 {hashlib.sha1(on_disk).hexdigest()[:12]} - "
             f"re-run the generator and commit the asset")
    else:
        ok(f"{label}: the shipped {shipped.name} is byte-for-byte what the generator makes ({len(on_disk)} bytes)")


def gate_bytes(label: str, data: bytes) -> None:
    cap = BYTE_CEILING[label]
    if len(data) > cap:
        fail(f"{label}: {len(data)} bytes over the {cap} ceiling (R9: do not let actors drift the way props did)")
    else:
        ok(f"{label}: {len(data)} bytes, {len(data) / cap:.0%} of its {cap} ceiling")
    if cap >= len(data) * 4:
        fail(f"{label}: the byte ceiling is {cap / len(data):.1f}x the asset and has stopped constraining it")


def gate_palette(label: str, atlas: np.ndarray, accent_keys=()) -> None:
    """check_char.py's palette discipline: no forbidden hues, and saturation past 0.35 only on accent parts."""
    rep = C._hsv_report(atlas)
    if rep["forbidden_hue_texels"] > 0.001 * rep["texels"]:
        fail(f"{label}: {rep['forbidden_hue_texels']} of {rep['texels']} texels in forbidden hues "
             f"(magenta/pink, or green through cyan)")
    elif rep["s_p95"] > 0.35:
        fail(f"{label}: saturation p95 {rep['s_p95']:.3f} over 0.35; these are not the player's one accent")
    else:
        ok(f"{label}: palette v_p99 {rep['v_p99']:.3f}, s_p95 {rep['s_p95']:.3f}, "
           f"forbidden-hue texels {rep['forbidden_hue_texels']}")


def gate_m3(label: str, surfaces, loft_only) -> None:
    m = L.m3_measure(surfaces)
    per = m["per_azimuth"]
    if m["spread"] < M3_SPREAD_MIN:
        fail(f"{label}: M3 spread {m['spread']:.4f} < {M3_SPREAD_MIN} at azimuth {m['worst_azimuth']:.0f}")
    else:
        ok(f"{label}: M3 spread {m['spread']:.4f} >= {M3_SPREAD_MIN} (worst azimuth {m['worst_azimuth']:.0f}; "
           + ", ".join(f"az {k:.0f} p10 {v['p10']:.4f} p90 {v['p90']:.4f}" for k, v in sorted(per.items())) + ")")
    if not loft_only:
        fail(f"{label}: the M3 negative control has no parts in it; it cannot fail")
        return
    bare = L.m3_measure(loft_only)
    if bare["spread"] >= M3_SPREAD_MIN:
        fail(f"{label}: the M3 gate passes the figure with its plates removed ({bare['spread']:.4f}); "
             f"it has stopped measuring the geometry")
    else:
        ok(f"{label}: the M3 gate is live - with every flat plate removed the same figure spreads "
           f"{bare['spread']:.4f}")


def gate_grounding(label: str, surfaces, want_height: float) -> None:
    y = np.concatenate([s.positions[:, 1] for s in surfaces])
    if abs(float(y.min())) > 1e-5:
        fail(f"{label}: not grounded, lowest vertex y = {float(y.min()):.6f}")
    else:
        ok(f"{label}: grounded, lowest vertex y = {float(y.min()):.6f}, standing {float(y.max()):.3f}")
    if abs(float(y.max()) - want_height) > 1e-4:
        fail(f"{label}: normalised height {float(y.max()):.4f} is not the {want_height} the engine gives it")


# ---------------------------------------------------------------------------- the villagers

def check_villagers() -> None:
    print("check_actors: the villagers")
    for name in N.VARIANTS:
        label = f"villager-{name}"
        a = gate_determinism(label, lambda n=name: N.build_asset(n), N.glb_bytes)
        data = N.glb_bytes(a)
        gate_shipped(label, data)
        gate_bytes(label, data)
        doc, _ = G._check_container(data)

        # rigid, exactly contract C1's six renderable nodes, exactly two clips
        if "skins" in doc:
            fail(f"{label}: carries a skin; a villager must stay a rigid part hierarchy or its node overrides "
                 f"all degrade to identity (finding F2)")
        c1 = renderable_errors(label, doc)
        for e in c1:
            fail(e)
        if not c1:
            ok(f"{label}: rigid, renderable nodes exactly {sorted(N.RENDERABLE)}")
        clips = {x["name"] for x in doc["animations"]}
        if clips != {"idle", "walk"}:
            fail(f"{label}: clips {sorted(clips)}, expected exactly ['idle', 'walk']")
        else:
            ok(f"{label}: clips {sorted(clips)}")
        if len(doc["materials"]) != 1 or "baseColorTexture" not in doc["materials"][0]["pbrMetallicRoughness"]:
            fail(f"{label}: expected one baked material carrying a baseColorTexture")

        surfaces = L.asset_surfaces(a, [a["encoded"]], world_height=VILLAGER_WORLD_HEIGHT)
        gate_grounding(label, surfaces, VILLAGER_WORLD_HEIGHT)
        gate_palette(label, a["atlas"])
        gate_m3(label, surfaces, [s for s, part in zip(surfaces, a["parts"]) if part[1].n is not None])

        # every clip keeps the figure out of the ground
        worst = 0.0
        for clip in a["animations"]:
            table = clip.rotations or clip.translations
            for k in range(len(next(iter(table.values()))[0])):
                posed = L.asset_surfaces(a, None, clip=clip, key=k, world_height=VILLAGER_WORLD_HEIGHT)
                worst = min(worst, min(float(s.positions[:, 1].min()) for s in posed))
        if worst < -0.03:
            fail(f"{label}: a clip pushes it {-worst:.3f} units into the ground")
        else:
            ok(f"{label}: no clip sinks it more than {-worst:.3f} units below y = 0")

    # The sibling for the node-name gate. It used to build a set by removing "head" and adding "skull" and then
    # test `set(N.RENDERABLE) <= renamed`, which reduces to `"head" in renamed` -> False from the fixture's own
    # constant: it asserted that the string "head" appears in a tuple in the generator, never called the gate,
    # and used subset where the gate uses equality. This runs the real check over the real exported document
    # with its head node renamed.
    doc, _ = G._check_container(N.glb_bytes(N.build_asset("vendor")))
    if renderable_errors("villager-vendor", doc):
        fail("the C1 node-name sibling cannot measure anything: the untampered vendor document already fails")
    renamed = copy.deepcopy(doc)
    heads = [n for n in renamed["nodes"] if n.get("name") == "head" and "mesh" in n]
    if not heads:
        fail("the C1 node-name sibling found no renderable node called `head` to rename")
    else:
        heads[0]["name"] = "skull"
        found = renderable_errors("villager-vendor.renamed", renamed)
        if not found:
            fail("the C1 node-name gate accepts a document whose head node is called `skull`; it has stopped "
                 "measuring the export")
        else:
            ok(f"the C1 node-name gate is live: renaming the exported `head` node to `skull` makes the same "
               f"comparison report - {found[0]}")


# ---------------------------------------------------------------------------- the hound

def _fk_error(asset: dict, inverse_bind: np.ndarray) -> float:
    """Worst disagreement, in world units, between CPU skinning (charforge.skin, in iso u) and a glTF-space
    forward-kinematic evaluation of the exported skin, over every key of every clip whose poses we hold.

    This is the mitigation for risk R4, the wave's one silent failure: bind_world() is in iso axes and world u,
    the export is glTF axes scaled by UNITS_PER_U, and a wrong conversion collapses or explodes the mesh on the
    first non-rest frame without anything throwing. Both sides are computed from the same authored Poses, so an
    agreement here means the exported matrices really do reproduce the animation that was authored."""
    sk, skins = asset["skeleton"], asset["skins"]
    ibm = np.asarray(inverse_bind, dtype=np.float64)
    worst = 0.0
    for clip_name, poses in asset["clip_poses"].items():
        clip = next(c for c in asset["animations"] if c.name == clip_name)
        for key in range(len(poses)):
            cpu = C.skin(sk, poses[key], asset["meshes"], skins)
            world = L.node_worlds(asset["nodes"], clip, key)
            for pi, (prim, res) in enumerate(zip(asset["primitives"], cpu)):
                want = np.stack((res.v[:, 0], res.v[:, 2], res.v[:, 1]), 1) * B.UNITS_PER_U
                mesh = asset["meshes"][pi]
                if mesh.n is None:
                    want = want[np.asarray(mesh.f, dtype=np.int64).reshape(-1)]
                v = np.asarray(prim.positions, dtype=np.float64)
                vb = np.hstack((v, np.ones((len(v), 1))))
                got = np.zeros((len(v), 3))
                for slot in range(2):
                    j = np.asarray(prim.joints, dtype=np.int64)[:, slot]
                    w = np.asarray(prim.weights, dtype=np.float64)[:, slot]
                    # glTF stores a matrix column-major, so the array gltfwrite writes is the transpose of the
                    # one numpy multiplies a column vector by.
                    M = np.einsum("nab,nbc->nac", np.stack([world[x] for x in j]),
                                  np.stack([ibm[x].T for x in j]))
                    got += w[:, None] * np.einsum("nab,nb->na", M, vb)[:, :3]
                worst = max(worst, float(np.abs(got - want).max()))
    return worst


def check_hound() -> None:
    print("check_actors: the hound")
    label = "hound"
    a = gate_determinism(label, B.build_asset, B.glb_bytes)
    data = B.glb_bytes(a)
    gate_shipped(label, data)
    gate_bytes(label, data)
    doc, _ = G._check_container(data)

    if "skins" not in doc or len(doc["skins"]) != 1:
        fail(f"{label}: expected exactly one skin")
        return
    joints = doc["skins"][0]["joints"]
    if len(joints) > MAX_JOINTS:
        fail(f"{label}: {len(joints)} joints over contract C3's {MAX_JOINTS}")
    else:
        ok(f"{label}: one skin, {len(joints)} joints (C3 cap {MAX_JOINTS}, engine cap {G.MAX_JOINTS})")
    if len(joints) >= G.MAX_JOINTS:
        fail(f"{label}: at {len(joints)} joints LoadSkinning would silently return null and draw bind pose")

    clips = {x["name"] for x in doc["animations"]}
    # Two gaits: the walk it patrols at and the gallop it chases at. A re-forge that dropped either one would
    # leave EnemyClipSet naming a clip the GLB does not carry, and the engine holds the pose instead of throwing.
    if clips != {"idle", "walk", "gallop", "attack", "die"}:
        fail(f"{label}: clips {sorted(clips)}, expected idle/walk/gallop/attack/die as BARE names")
    else:
        ok(f"{label}: clips {sorted(clips)}")

    # every primitive skinned, weights summing to 1
    bad = [p for p in a["primitives"] if p.joints is None or p.weights is None]
    if bad:
        fail(f"{label}: {len(bad)} primitives carry no JOINTS_0/WEIGHTS_0")
    else:
        ok(f"{label}: all {len(a['primitives'])} primitives carry JOINTS_0 and WEIGHTS_0")
    sums = weight_sums(a["primitives"])
    if np.abs(sums - 1.0).max() > WEIGHT_SUM_TOL:
        fail(f"{label}: a weight row sums to {sums[np.argmax(np.abs(sums - 1.0))]:.6f}, not 1")
    else:
        ok(f"{label}: every weight row sums to 1 (worst deviation {np.abs(sums - 1.0).max():.2e})")
    idx = np.concatenate([np.asarray(p.joints, dtype=np.int64).reshape(-1) for p in a["primitives"]])
    if idx.min() < 0 or idx.max() >= len(joints):
        fail(f"{label}: a joint index is outside the skin")
    # The blend floor. This was an ok() with no comparison in it at all: the parenthetical named the failure mode
    # (a rigid-only bind reads 0) and then did not test for it, so a hound bound entirely rigid printed an ok and
    # the gate exited 0.
    blended = blended_share(a["primitives"])
    if blended < BLENDED_MIN:
        fail(f"{label}: only {blended:.1%} of vertices take a second joint's influence, under the "
             f"{BLENDED_MIN:.0%} floor; this bind is rigid, not skinned")
    else:
        ok(f"{label}: {blended:.1%} of vertices take a second joint's influence, over the {BLENDED_MIN:.0%} "
           f"floor (a rigid-only bind reads 0)")

    # The sibling for the weight-sum gate. It used to ASSIGN tampered[0] = 0.5 and test `max(|t - 1|) <= 1e-6`,
    # which reduces to `0.5 <= 1e-6` - false unconditionally - on a copy of the gate's own summary statistic.
    # This tampers the WEIGHTS_0 array a primitive is built from, which is the mistake the gate exists to catch
    # (a renormalisation dropped from the bind), and re-runs the same comparison over the real primitive set.
    p0 = a["primitives"][0]
    w = np.array(p0.weights, dtype=np.float64, copy=True)
    w[0] *= 0.5
    tampered = [dataclasses.replace(p0, weights=w), *a["primitives"][1:]]
    dev = float(np.abs(weight_sums(tampered) - 1.0).max())
    if dev <= WEIGHT_SUM_TOL:
        fail(f"the weight-sum gate accepts a primitive whose first WEIGHTS_0 row was halved (worst deviation "
             f"{dev:.2e}); it has stopped measuring the weights")
    else:
        ok(f"the weight-sum gate is live: halving one row of the first primitive's WEIGHTS_0 moves the worst "
           f"deviation to {dev:.3f}, over the {WEIGHT_SUM_TOL:.0e} tolerance")

    # The sibling for the blend floor: the rigid-only bind it names. Every vertex welded to its first joint, the
    # same arrays otherwise, measured by the same function.
    rigid = []
    for prim in a["primitives"]:
        rw = np.array(prim.weights, dtype=np.float64, copy=True)
        rw[:, 0], rw[:, 1:] = 1.0, 0.0
        rigid.append(dataclasses.replace(prim, weights=rw))
    rigid_share = blended_share(rigid)
    if rigid_share >= BLENDED_MIN:
        fail(f"the blend floor accepts a rigid-only bind ({rigid_share:.1%}); it has stopped measuring")
    else:
        ok(f"the blend floor is live: welding every vertex to its first joint takes the same measurement to "
           f"{rigid_share:.1%}, under the {BLENDED_MIN:.0%} floor")

    # R4: the exported matrices reproduce the authored animation
    err = _fk_error(a, a["inverse_bind"])
    if err > FK_TOLERANCE:
        fail(f"{label}: CPU skinning and the glTF forward kinematics disagree by {err:.3e} world units "
             f"(> {FK_TOLERANCE}); the inverse-bind conversion is wrong (risk R4)")
    else:
        ok(f"{label}: CPU skinning agrees with a glTF-space forward-kinematic evaluation to {err:.2e} world "
           f"units, over every key of every clip")
    # the sibling: the exact mistake R4 names - an inverse bind built in iso u, with UNITS_PER_U forgotten
    unscaled = _fk_error(a, C.inverse_bind_export(a["skeleton"], 1.0))
    if unscaled <= FK_TOLERANCE:
        fail("the skinning cross-check passes an inverse bind with the export scale left out; "
             "it has stopped measuring the conversion")
    else:
        ok(f"the skinning cross-check is live: dropping UNITS_PER_U from the inverse bind moves vertices by "
           f"{unscaled:.2f} world units")

    surfaces = L.asset_surfaces(a, [a["encoded"]], world_height=HOUND_WORLD_HEIGHT)
    gate_grounding(label, surfaces, HOUND_WORLD_HEIGHT)
    gate_palette(label, a["atlas"])
    gate_m3(label, surfaces, [s for s, part in zip(surfaces, a["parts"]) if part[0].n is not None])

    # the sibling for grounding: the hound before it is dropped to z = 0 must fail the same test
    if a["drop"] == 0.0:
        fail("the grounding gate has nothing to catch: the authored skeleton already stood on z = 0")
    else:
        ok(f"the grounding gate is live: as authored the hound's lowest vertex sat {a['drop']:.3f} u off the "
           f"ground and every mesh is dropped by it")

    # the sibling for the joint cap: the writer itself must refuse a skin over the engine's limit
    try:
        G.build_glb(a["nodes"], a["primitives"], list(range(G.MAX_JOINTS + 1)), np.zeros((G.MAX_JOINTS + 1, 4, 4)),
                    [], a["materials"])
    except ValueError:
        ok(f"the joint cap is live: gltfwrite refuses a skin of {G.MAX_JOINTS + 1} joints")
    else:
        fail(f"gltfwrite accepted a skin of {G.MAX_JOINTS + 1} joints, past the engine's uniform array")


# ---------------------------------------------------------------------------- the risen skeleton

SKELETON_WORLD_HEIGHT = L.PLAYER_WORLD_HEIGHT   # a biped: the player's height, not the hound's 0.60
CORPSE_REST_TOL = 0.30        # u: how far the final `die` pose's lowest vertex may sit off z = 0
STAND_FLOOR_TOL = 0.50        # u: no key of idle/walk/attack may sink this far under the floor (hound walk: 0.45)
LOOP_TOL = 1e-6               # degrees / u: a looping clip must close on its opening pose to this
HEAD_MIN_EXTENT_U = 2.5       # u: the head's smallest bind-pose extent; the cranium is ~3.5 across, plates are 0.3
HEAD_MIN_SCREEN_U = 2.8       # u: the head's smallest on-screen extent (iso camera) in the held die pose


def clip_set_errors(names) -> list[str]:
    """Contract C5: EXACTLY the four bare names, each once. A duplicate throws on load; an unknown name does not
    throw, it just stops and holds the pose, which looks like a posing bug."""
    out = []
    if sorted(set(names)) != ["attack", "die", "idle", "walk"]:
        out.append(f"clips {sorted(names)}, expected exactly idle/walk/attack/die as BARE names")
    if len(set(names)) != len(list(names)):
        out.append("a clip name appears twice")
    return out


def die_descent_errors(lows) -> str:
    """The per-key lowest vertex of `die`: it may not sit meaningfully under the final (resting) height, and
    it may not rise back out of the floor over the last keys."""
    rest = lows[-1]
    if min(lows) < rest - CORPSE_REST_TOL:
        return f"sinks {rest - min(lows):.2f} u under the resting height"
    if any(lows[i + 1] > lows[i] + CORPSE_REST_TOL for i in range(len(lows) - 1)):
        return "pops back up out of the floor"
    return ""


def pose_gap(p, q) -> float:
    """Largest difference between two charforge Poses (a joint missing from one is zero), degrees or u."""
    keys = set(p.rot) | set(q.rot)
    d = [abs(x - y) for k in keys for x, y in zip(p.rot.get(k, (0, 0, 0)), q.rot.get(k, (0, 0, 0)))]
    d += [abs(x - y) for x, y in zip(p.root_move, q.root_move)]
    return float(max(d)) if d else 0.0


def _screen_minor(pts) -> float:
    """Smaller extent of a point set projected through the shipped iso camera (yaw 45, pitch 50), in u, along
    its principal axes: an edge-on disc is thin however it is turned."""
    yaw, pitch = np.radians(45.0), np.radians(50.0)
    x = pts[:, 0] * np.cos(yaw) - pts[:, 1] * np.sin(yaw)
    y = pts[:, 0] * np.sin(yaw) + pts[:, 1] * np.cos(yaw)
    sy = pts[:, 2] * np.cos(pitch) + y * np.sin(pitch)
    uv = np.stack((x, sy), 1)
    uv = uv - uv.mean(0)
    _, vt = np.linalg.eigh(np.cov(uv.T))
    return float(np.ptp(uv @ vt, axis=0).min())


def _lowest_z(asset: dict, pose) -> float:
    """Lowest vertex, in iso u, of the whole mesh posed by CPU skinning."""
    res = C.skin(asset["skeleton"], pose, asset["meshes"], asset["skins"])
    return float(min(float(np.asarray(r.v)[:, 2].min()) for r in res))


def check_skeleton() -> None:
    print("check_actors: the risen skeleton")
    label = "skeleton"
    a = gate_determinism(label, K.build_asset, K.glb_bytes)
    data = K.glb_bytes(a)
    gate_shipped(label, data)
    gate_bytes(label, data)
    doc, _ = G._check_container(data)

    if "skins" not in doc or len(doc["skins"]) != 1:
        fail(f"{label}: expected exactly one skin")
        return
    joints = doc["skins"][0]["joints"]
    if len(joints) > MAX_JOINTS or len(joints) >= G.MAX_JOINTS:
        fail(f"{label}: {len(joints)} joints over contract C3's {MAX_JOINTS} / the engine's {G.MAX_JOINTS}")
    else:
        ok(f"{label}: one skin, {len(joints)} joints (C3 cap {MAX_JOINTS}, engine cap {G.MAX_JOINTS})")
    # the sibling: a skin over the cap must be refused by the generator itself, not just noticed here
    over = tuple(K.JOINTS) + tuple(C.Joint(f"extra-{i}", "root", (0.0, 0.0, 0.0)) for i in range(MAX_JOINTS))
    saved = K.JOINTS
    K.JOINTS = over
    try:
        K.build_asset()
    except ValueError:
        ok(f"the joint cap is live: the generator refuses {len(over)} joints")
    else:
        fail(f"the generator accepted {len(over)} joints, past contract C3's {MAX_JOINTS}")
    finally:
        K.JOINTS = saved

    # clips: exactly the four bare names (a duplicate throws on load, an unknown name holds the pose silently)
    names = [x["name"] for x in doc["animations"]]
    problems = clip_set_errors(names)
    if problems:
        fail(f"{label}: {'; '.join(problems)}")
    else:
        ok(f"{label}: clips {sorted(names)}")
    # the siblings run the SAME function on the mistakes it exists to catch
    for what, bad_names in (("the hound's extra gallop", [*names, "gallop"]),
                            ("a pack-style prefixed name", ["idle", "AnimalArmature|Walk", "attack", "die"]),
                            ("a duplicate name, which throws on load", [*names, "die"]),
                            ("a missing clip", [n for n in names if n != "walk"])):
        if not clip_set_errors(bad_names):
            fail(f"the clip-set gate accepts {what}; it has stopped measuring")
        else:
            ok(f"the clip-set gate is live: it rejects {what}")

    bad = [p for p in a["primitives"] if p.joints is None or p.weights is None]
    if bad:
        fail(f"{label}: {len(bad)} primitives carry no JOINTS_0/WEIGHTS_0")
    else:
        ok(f"{label}: all {len(a['primitives'])} primitives carry JOINTS_0 and WEIGHTS_0")
    sums = weight_sums(a["primitives"])
    if np.abs(sums - 1.0).max() > WEIGHT_SUM_TOL:
        fail(f"{label}: a weight row sums to {sums[np.argmax(np.abs(sums - 1.0))]:.6f}, not 1")
    else:
        ok(f"{label}: every weight row sums to 1 (worst deviation {np.abs(sums - 1.0).max():.2e})")
    p0 = a["primitives"][0]
    w = np.array(p0.weights, dtype=np.float64, copy=True)
    w[0] *= 0.5
    dev = float(np.abs(weight_sums([dataclasses.replace(p0, weights=w), *a["primitives"][1:]]) - 1.0).max())
    if dev <= WEIGHT_SUM_TOL:
        fail("the weight-sum gate accepts a halved WEIGHTS_0 row; it has stopped measuring the weights")
    else:
        ok(f"the weight-sum gate is live: halving one row moves the worst deviation to {dev:.3f}")

    idx = np.concatenate([np.asarray(p.joints, dtype=np.int64).reshape(-1) for p in a["primitives"]])
    if idx.min() < 0 or idx.max() >= len(joints):
        fail(f"{label}: a joint index is outside the skin")
    else:
        ok(f"{label}: every joint index lies inside the {len(joints)}-joint skin")
    # the sibling: a skin that lost a joint (the last joint dropped) leaves every vertex bound to it outside the
    # skin, and the same range test over the real primitives must catch it
    short = len(joints) - 1
    if idx.max() < short:
        fail("the joint-index gate has nothing to catch: no vertex is bound to the last joint")
    else:
        ok(f"the joint-index gate is live: against a skin one joint short ({short}), index {int(idx.max())} "
           f"falls outside")

    blended = blended_share(a["primitives"])
    if blended < BLENDED_MIN:
        fail(f"{label}: only {blended:.1%} of vertices take a second joint's influence; this bind is rigid")
    else:
        ok(f"{label}: {blended:.1%} of vertices take a second joint's influence, over the {BLENDED_MIN:.0%} floor")
    rigid = []
    for prim in a["primitives"]:
        rw = np.array(prim.weights, dtype=np.float64, copy=True)
        rw[:, 0], rw[:, 1:] = 1.0, 0.0
        rigid.append(dataclasses.replace(prim, weights=rw))
    if blended_share(rigid) >= BLENDED_MIN:
        fail("the blend floor accepts a rigid-only bind; it has stopped measuring")
    else:
        ok(f"the blend floor is live: a rigid-only bind reads {blended_share(rigid):.1%}")

    err = _fk_error(a, a["inverse_bind"])
    if err > FK_TOLERANCE:
        fail(f"{label}: CPU skinning and glTF forward kinematics disagree by {err:.3e} world units")
    else:
        ok(f"{label}: CPU skinning agrees with glTF forward kinematics to {err:.2e} world units, every key of "
           f"every clip")
    unscaled = _fk_error(a, C.inverse_bind_export(a["skeleton"], 1.0))
    if unscaled <= FK_TOLERANCE:
        fail("the skinning cross-check passes an inverse bind with the export scale left out")
    else:
        ok(f"the skinning cross-check is live: dropping UNITS_PER_U moves vertices by {unscaled:.2f} world units")

    surfaces = L.asset_surfaces(a, [a["encoded"]], world_height=SKELETON_WORLD_HEIGHT)
    gate_grounding(label, surfaces, SKELETON_WORLD_HEIGHT)
    z_bind = min(float(np.asarray(m.v)[:, 2].min()) for m in a["meshes"])
    if abs(z_bind) > 1e-4:
        fail(f"{label}: the exported bind pose's lowest vertex is at z = {z_bind:.4f} u, not 0")
    else:
        ok(f"{label}: the exported bind pose's lowest vertex sits at z = {z_bind:.4f} u")
    if a["drop"] == 0.0:
        fail("the grounding gate has nothing to catch: the authored figure already stood on z = 0")
    else:
        ok(f"the grounding gate is live: as authored the lowest vertex sat {a['drop']:.3f} u off the ground")
    gate_palette(label, a["atlas"])
    gate_m3(label, surfaces, [s for s, part in zip(surfaces, a["parts"]) if part[0].n is not None])

    # `die` descends to the ground and never under it: no key may sit below the corpse's resting height by more
    # than the tolerance, and the lowest point may not rise back out of the floor (a non-monotone drop pops)
    corpse = a["clip_poses"]["die"][-1]
    lows = [_lowest_z(a, p) for p in a["clip_poses"]["die"]]
    low = lows[-1]
    if abs(low) > CORPSE_REST_TOL:
        fail(f"{label}: the held `die` frame's lowest vertex is {low:.2f} u off the ground")
    else:
        ok(f"{label}: the held `die` frame rests on the ground (lowest vertex {low:+.2f} u)")
    problems = die_descent_errors(lows)
    if problems:
        fail(f"{label}: die {problems} (per-key lowest vertex {[round(x, 2) for x in lows]})")
    else:
        ok(f"{label}: die never sinks under the floor or pops back out of it (per-key lowest "
           f"{[round(x, 2) for x in lows]})")
    floating = _lowest_z(a, C.Pose(corpse.rot, (0.0, corpse.root_move[1], 0.0)))
    if abs(floating) <= CORPSE_REST_TOL:
        fail("the corpse gate accepts a corpse whose root was never dropped; it has stopped measuring")
    else:
        ok(f"the corpse gate is live: without the root drop the same pose sits {floating:+.2f} u off the ground")
    # the sibling for the descent: the earlier authoring, which sank 4.9 u under the floor and then came back up
    old = [0.0, -2.99, -3.73, -4.88, -0.01]
    if not die_descent_errors(old):
        fail("the die-descent gate accepts the sink-then-pop drop it was written for")
    else:
        ok("the die-descent gate is live: the old drop (-4.88 u then back to -0.01 u) is rejected")

    # no key of a standing clip drives the figure through the floor (the hound's attack never leaves 0.000)
    for name in ("idle", "walk", "attack"):
        worst = min(_lowest_z(a, p) for p in a["clip_poses"][name])
        if worst < -STAND_FLOOR_TOL:
            fail(f"{label}: `{name}` drives the figure {-worst:.2f} u under the floor")
        else:
            ok(f"{label}: `{name}` stays within {STAND_FLOOR_TOL} u of the floor (worst {worst:+.2f} u)")
    # the old attack key 3: the same lunge pose with the root dipped 0.9 u on top of it
    deep = _lowest_z(a, C.Pose(a["clip_poses"]["attack"][3].rot, (0.0, 0.9, -0.9)))
    if deep >= -STAND_FLOOR_TOL:
        fail("the floor gate accepts the old attack lunge (root dipped 0.9 u); it has stopped measuring")
    else:
        ok(f"the floor gate is live: the old 0.9 u dip puts the lunge {-deep:.2f} u under the floor")

    # clips that loop must close on their opening pose, to a tolerance (walk's last key is the first, to float)
    for name in ("idle", "walk", "attack"):
        gap = pose_gap(a["clip_poses"][name][0], a["clip_poses"][name][-1])
        if gap > LOOP_TOL:
            fail(f"{label}: `{name}` closes {gap:.3g} off its opening pose and will pop when it loops")
        else:
            ok(f"{label}: `{name}` closes on its opening pose (gap {gap:.1e})")
    walk = a["clip_poses"]["walk"]
    broken = pose_gap(walk[0], K._gait(math.tau * 7.5 / 8.0))
    if broken <= LOOP_TOL:
        fail("the loop-close gate accepts a walk that ends half a step early; it has stopped measuring")
    else:
        ok(f"the loop-close gate is live: a walk ending half a step early opens a gap of {broken:.1f}")

    # THE HEAD. A stack of flat plates reads as a straw hat and vanishes edge-on; none of the gates above can
    # see that. The head must be a volume in bind, and must still occupy a real patch of the screen in the
    # held `die` frame - the pose the player looks at for the whole death linger.
    head_ix = K.INDEX["head"]

    def head_points(pose, only_flat=False):
        res = C.skin(a["skeleton"], pose, a["meshes"], a["skins"])
        pts = [np.asarray(r.v)[sk.i0 == head_ix] for r, sk, m in zip(res, a["skins"], a["meshes"])
               if not only_flat or m.n is None]
        return np.vstack(pts)

    for what, flat in (("the head", False), ("a head of flat plates only", True)):
        ext = np.ptp(head_points(C.Pose({}), flat), axis=0)
        if flat:
            if ext.min() >= HEAD_MIN_EXTENT_U:
                fail(f"the head-volume gate accepts {what} (extents {ext.round(2)}); it has stopped measuring")
            else:
                ok(f"the head-volume gate is live: {what} has a smallest extent of {ext.min():.2f} u")
        elif ext.min() < HEAD_MIN_EXTENT_U:
            fail(f"{label}: the head is flat, extents {ext.round(2)} u (floor {HEAD_MIN_EXTENT_U})")
        else:
            ok(f"{label}: the head is a volume in bind, extents {ext.round(2)} u (floor {HEAD_MIN_EXTENT_U})")
    for what, flat in (("the held die head", False), ("a flat-plate head in the held die pose", True)):
        minor = _screen_minor(head_points(corpse, flat))
        if flat:
            if minor >= HEAD_MIN_SCREEN_U:
                fail(f"the die-head gate accepts {what} (screen minor extent {minor:.2f} u); stopped measuring")
            else:
                ok(f"the die-head gate is live: {what} covers only {minor:.2f} u on screen")
        elif minor < HEAD_MIN_SCREEN_U:
            fail(f"{label}: the head covers only {minor:.2f} u of screen in the held die pose "
                 f"(floor {HEAD_MIN_SCREEN_U})")
        else:
            ok(f"{label}: the head still covers {minor:.2f} u of screen in the held die pose "
               f"(floor {HEAD_MIN_SCREEN_U})")


def main() -> int:
    check_villagers()
    check_hound()
    check_skeleton()
    print(f"check_actors: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main())
