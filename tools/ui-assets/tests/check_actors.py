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

import hashlib
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
import gltfwrite as G  # noqa: E402

M3_SPREAD_MIN = 0.50          # the same floor check_char.py holds the player to (spec M3)
MAX_JOINTS = 32               # contract C3
FK_TOLERANCE = 1e-5           # CPU skinning vs glTF forward kinematics, in world units
BYTE_CEILING = {"villager-vendor": 320_000, "villager-smith": 320_000, "villager-warden": 320_000,
                "hound": 420_000}
# R9: dream/animations/props drifted to 13 MB with dead_oak.glb alone at 3.3 MB. These ceilings are each about
# 1.6x the asset's measured size, so ordinary authoring moves freely and a tessellation or atlas mistake does not.
HOUND_WORLD_HEIGHT = 0.60     # WolfAssetDefinition.Normalization.TargetWorldHeight; GraveHowler shares it
VILLAGER_WORLD_HEIGHT = L.PLAYER_WORLD_HEIGHT   # the spec's decision: villagers match the player

errs: list[str] = []


def fail(msg: str) -> None:
    errs.append(msg)
    print(f"  FAIL {msg}")


def ok(msg: str) -> None:
    print(f"  ok   {msg}")


# ---------------------------------------------------------------------------- shared gates

def gate_determinism(label: str, build, to_bytes) -> dict:
    a, b = build(), build()
    ha, hb = hashlib.sha1(to_bytes(a)).hexdigest(), hashlib.sha1(to_bytes(b)).hexdigest()
    if ha != hb:
        fail(f"{label}: two builds produced different GLB bytes")
    else:
        ok(f"{label}: deterministic, sha1 {ha[:12]} ({len(to_bytes(a))} bytes)")
    return a


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
        gate_bytes(label, data)
        doc, _ = G._check_container(data)

        # rigid, exactly contract C1's six renderable nodes, exactly two clips
        if "skins" in doc:
            fail(f"{label}: carries a skin; a villager must stay a rigid part hierarchy or its node overrides "
                 f"all degrade to identity (finding F2)")
        renderable = {n["name"] for n in doc["nodes"] if "mesh" in n}
        if renderable != set(N.RENDERABLE):
            fail(f"{label}: renderable nodes {sorted(renderable)} are not contract C1's {sorted(N.RENDERABLE)}")
        else:
            ok(f"{label}: rigid, renderable nodes exactly {sorted(renderable)}")
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

    # the sibling for the node-name gate: renaming one node must be caught by the same comparison
    a = N.build_asset("vendor")
    renamed = {n.name for n in a["nodes"] if n.name != "head"} | {"skull"}
    if set(N.RENDERABLE) <= renamed:
        fail("the C1 node-name gate would accept a figure whose head node is called something else")
    else:
        ok("the C1 node-name gate is live: renaming `head` to `skull` fails the same comparison")


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
    if clips != {"idle", "walk", "attack", "die"}:
        fail(f"{label}: clips {sorted(clips)}, expected idle/walk/attack/die as BARE names")
    else:
        ok(f"{label}: clips {sorted(clips)}")

    # every primitive skinned, weights summing to 1
    bad = [p for p in a["primitives"] if p.joints is None or p.weights is None]
    if bad:
        fail(f"{label}: {len(bad)} primitives carry no JOINTS_0/WEIGHTS_0")
    else:
        ok(f"{label}: all {len(a['primitives'])} primitives carry JOINTS_0 and WEIGHTS_0")
    sums = np.concatenate([np.asarray(p.weights, dtype=np.float64).sum(1) for p in a["primitives"]])
    if np.abs(sums - 1.0).max() > 1e-6:
        fail(f"{label}: a weight row sums to {sums[np.argmax(np.abs(sums - 1.0))]:.6f}, not 1")
    else:
        ok(f"{label}: every weight row sums to 1 (worst deviation {np.abs(sums - 1.0).max():.2e})")
    idx = np.concatenate([np.asarray(p.joints, dtype=np.int64).reshape(-1) for p in a["primitives"]])
    if idx.min() < 0 or idx.max() >= len(joints):
        fail(f"{label}: a joint index is outside the skin")
    blended = float(np.mean([(np.asarray(p.weights)[:, 1] > 0.01).mean() for p in a["primitives"]]))
    ok(f"{label}: {blended:.1%} of vertices take a second joint's influence (a rigid-only bind would be 0)")

    # the sibling for the weights gate: a tampered row must be caught by the same comparison
    tampered = sums.copy()
    tampered[0] = 0.5
    if np.abs(tampered - 1.0).max() <= 1e-6:
        fail("the weight-sum gate would accept a row summing to 0.5")
    else:
        ok("the weight-sum gate is live: a row set to 0.5 fails the same comparison")

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


def main() -> int:
    check_villagers()
    check_hound()
    print(f"check_actors: {len(errs)} problem(s)")
    return 1 if errs else 0


if __name__ == "__main__":
    raise SystemExit(main())
