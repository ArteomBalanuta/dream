"""Minimal deterministic binary glTF (.glb) writer for skinned, animated meshes.

Pure stdlib + numpy + pillow. Targets SharpGLTF.Core 1.0.6 and the dream loader:
POSITION/NORMAL/TEXCOORD_0/JOINTS_0/WEIGHTS_0, <=64 joints, TRS animation
channels, embedded PNG textures.
"""
from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field

import numpy as np

FLOAT, USHORT, UINT = 5126, 5123, 5125
ARRAY_BUFFER, ELEMENT_ARRAY_BUFFER = 34962, 34963
MAX_JOINTS = 64


@dataclass
class GltfNode:
    name: str
    parent: int | None
    translation: tuple = (0.0, 0.0, 0.0)
    rotation: tuple = (0.0, 0.0, 0.0, 1.0)  # xyzw
    scale: tuple = (1.0, 1.0, 1.0)


@dataclass
class GltfPrimitive:
    positions: np.ndarray  # (V,3) float32
    normals: np.ndarray  # (V,3) float32
    uvs: np.ndarray  # (V,2) float32
    indices: np.ndarray  # (T,3) uint32
    joints: np.ndarray | None  # (V,4) uint16, indices into the skin; None when unskinned
    weights: np.ndarray | None  # (V,4) float32; None when unskinned
    material: int
    tangents: np.ndarray | None = None  # optional (V,4) float32
    node: int | None = None  # rigid case: node that owns this mesh; None = extra root node


@dataclass
class GltfAnimation:
    name: str
    rotations: dict = field(default_factory=dict)  # node -> (times (K,), quats (K,4))
    translations: dict = field(default_factory=dict)  # node -> (times (K,), vec3 (K,3))


class _Buffer:
    """Accumulates bufferViews + accessors; every view starts 4-byte aligned."""

    def __init__(self):
        self.data = bytearray()
        self.views = []
        self.accessors = []

    def add_view(self, raw: bytes, target: int | None = None) -> int:
        while len(self.data) % 4:
            self.data.append(0)
        view = {"buffer": 0, "byteOffset": len(self.data), "byteLength": len(raw)}
        if target is not None:
            view["target"] = target
        self.data += raw
        self.views.append(view)
        return len(self.views) - 1

    def add_accessor(self, arr: np.ndarray, ctype: int, dtype, atype: str,
                     target: int | None = None, minmax: bool = False) -> int:
        arr = np.ascontiguousarray(arr, dtype=dtype)
        ncomp = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[atype]
        arr = arr.reshape(-1, ncomp)
        acc = {"bufferView": self.add_view(arr.tobytes(), target),
               "componentType": ctype, "count": int(arr.shape[0]), "type": atype}
        if minmax:
            acc["min"] = [float(x) for x in arr.min(axis=0)]
            acc["max"] = [float(x) for x in arr.max(axis=0)]
        self.accessors.append(acc)
        return len(self.accessors) - 1


def _png_valid(data: bytes) -> bytes:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("texture is not PNG data")
    return data


def _normalise_weights(w: np.ndarray) -> np.ndarray:
    w = np.asarray(w, dtype=np.float64)
    s = w.sum(axis=1, keepdims=True)
    if np.any(s <= 0):
        raise ValueError("vertex with zero total skin weight")
    return (w / s).astype(np.float32)


def _pad(b: bytes, pad: bytes) -> bytes:
    return b + pad * (-len(b) % 4)


def build_glb(nodes, primitives, skin_joints, inverse_bind, animations, materials) -> bytes:
    skinned = skin_joints is not None and len(skin_joints) > 0
    if skinned:
        if len(skin_joints) > MAX_JOINTS:
            raise ValueError(f"skin has {len(skin_joints)} joints, max {MAX_JOINTS}")
        inverse_bind = np.asarray(inverse_bind, dtype=np.float32)
        if inverse_bind.shape != (len(skin_joints), 4, 4):
            raise ValueError("inverse_bind must be (J,4,4)")

    buf = _Buffer()
    children = {i: [] for i in range(len(nodes))}
    roots = []
    for i, n in enumerate(nodes):
        if n.parent is None:
            roots.append(i)
        else:
            children[n.parent].append(i)

    # Nodes: the skinned mesh lives on one extra node appended after the hierarchy.
    gnodes = []
    for i, n in enumerate(nodes):
        d = {"name": n.name}
        if tuple(n.translation) != (0.0, 0.0, 0.0):
            d["translation"] = [float(x) for x in n.translation]
        if tuple(n.rotation) != (0.0, 0.0, 0.0, 1.0):
            d["rotation"] = [float(x) for x in n.rotation]
        if tuple(n.scale) != (1.0, 1.0, 1.0):
            d["scale"] = [float(x) for x in n.scale]
        if children[i]:
            d["children"] = children[i]
        gnodes.append(d)

    # Meshes: primitives sharing a `node` form one mesh on that node. Primitives with
    # node=None form one mesh on an extra root node appended after the hierarchy.
    groups = {}  # node index (or None) -> list of primitive json, insertion ordered
    for p in primitives:
        v = len(p.positions)
        checks = [("normals", p.normals, 3), ("uvs", p.uvs, 2)]
        if p.tangents is not None:
            checks.append(("tangents", p.tangents, 4))
        if skinned:
            if p.joints is None or p.weights is None:
                raise ValueError("skinned asset needs joints and weights on every primitive")
            checks += [("joints", p.joints, 4), ("weights", p.weights, 4)]
        for name, a, w in checks:
            if np.asarray(a).shape != (v, w):
                raise ValueError(f"{name} must be ({v},{w})")
        if len(p.indices) and int(np.max(p.indices)) >= v:
            raise ValueError("index out of range")
        if p.node is not None and not 0 <= p.node < len(nodes):
            raise ValueError("primitive node out of range")
        attrs = {
            "POSITION": buf.add_accessor(p.positions, FLOAT, np.float32, "VEC3", ARRAY_BUFFER, True),
            "NORMAL": buf.add_accessor(p.normals, FLOAT, np.float32, "VEC3", ARRAY_BUFFER),
            "TEXCOORD_0": buf.add_accessor(p.uvs, FLOAT, np.float32, "VEC2", ARRAY_BUFFER),
        }
        if p.tangents is not None:
            attrs["TANGENT"] = buf.add_accessor(p.tangents, FLOAT, np.float32, "VEC4", ARRAY_BUFFER)
        if skinned:
            if len(p.joints) and int(np.max(p.joints)) >= len(skin_joints):
                raise ValueError("joint index outside skin")
            attrs["JOINTS_0"] = buf.add_accessor(p.joints, USHORT, np.uint16, "VEC4", ARRAY_BUFFER)
            attrs["WEIGHTS_0"] = buf.add_accessor(_normalise_weights(p.weights), FLOAT, np.float32,
                                                  "VEC4", ARRAY_BUFFER)
        idx = buf.add_accessor(p.indices, UINT, np.uint32, "SCALAR", ELEMENT_ARRAY_BUFFER)
        groups.setdefault(p.node, []).append(
            {"attributes": attrs, "indices": idx, "mode": 4, "material": int(p.material)})

    gmeshes = []
    scene_nodes = list(roots)
    for key, plist in groups.items():
        gmeshes.append({"name": "Mesh" if key is None else nodes[key].name, "primitives": plist})
        if key is None:
            gnodes.append({"name": "Mesh", "mesh": len(gmeshes) - 1})
            scene_nodes.append(len(gnodes) - 1)
            target = gnodes[-1]
        else:
            target = gnodes[key]
            target["mesh"] = len(gmeshes) - 1
        if skinned:
            target["skin"] = 0

    # Skin (optional)
    skin = None
    if skinned:
        ibm = buf.add_accessor(inverse_bind.reshape(-1, 16), FLOAT, np.float32, "MAT4")
        skin = {"joints": [int(j) for j in skin_joints], "inverseBindMatrices": ibm}
        if roots:
            skin["skeleton"] = roots[0]

    # Materials / textures / images
    gmats, textures, images = [], [], []
    for m in materials:
        pbr = {"baseColorFactor": [float(x) for x in m.get("base_color_factor", (1, 1, 1, 1))],
               "metallicFactor": float(m.get("metallic", 0.0)),
               "roughnessFactor": float(m.get("roughness", 1.0))}
        png = m.get("base_color_png")
        if png:
            view = buf.add_view(_png_valid(bytes(png)))
            images.append({"bufferView": view, "mimeType": "image/png"})
            textures.append({"source": len(images) - 1})
            pbr["baseColorTexture"] = {"index": len(textures) - 1}
        gm = {"name": m.get("name", "material"), "pbrMetallicRoughness": pbr}
        # Optional, absent from every existing asset (so their bytes do not change): a cutout material and an
        # emissive colour (linear, added after exposure by the engine's actor shader).
        if m.get("alpha_mode") in ("MASK", "BLEND"):
            gm["alphaMode"] = m["alpha_mode"]
            if m["alpha_mode"] == "MASK":
                gm["alphaCutoff"] = float(m.get("alpha_cutoff", 0.5))
        if m.get("emissive_factor") is not None:
            gm["emissiveFactor"] = [float(x) for x in m["emissive_factor"]]
        if m.get("double_sided"):
            gm["doubleSided"] = True
        gmats.append(gm)

    # Animations
    ganims = []
    for a in animations:
        samplers, channels = [], []
        for path, table, atype in (("rotation", a.rotations, "VEC4"),
                                   ("translation", a.translations, "VEC3")):
            for node in sorted(table):
                times, vals = table[node]
                times = np.asarray(times, dtype=np.float32).reshape(-1)
                vals = np.asarray(vals, dtype=np.float32)
                if len(times) != len(vals) or len(times) == 0:
                    raise ValueError(f"animation {a.name}: bad keyframes for node {node}")
                if np.any(np.diff(times) <= 0):
                    raise ValueError(f"animation {a.name}: times must be strictly ascending")
                if path == "rotation":
                    vals = vals / np.linalg.norm(vals, axis=1, keepdims=True)
                t_acc = buf.add_accessor(times, FLOAT, np.float32, "SCALAR", None, True)
                v_acc = buf.add_accessor(vals, FLOAT, np.float32, atype)
                samplers.append({"input": t_acc, "output": v_acc, "interpolation": "LINEAR"})
                channels.append({"sampler": len(samplers) - 1,
                                 "target": {"node": int(node), "path": path}})
        ganims.append({"name": a.name, "samplers": samplers, "channels": channels})

    doc = {
        "asset": {"version": "2.0", "generator": "dream gltfwrite.py"},
        "scene": 0,
        "scenes": [{"nodes": scene_nodes}],
        "nodes": gnodes,
        "meshes": gmeshes,
        "materials": gmats,
        "accessors": buf.accessors,
        "bufferViews": buf.views,
        "buffers": [{"byteLength": len(buf.data)}],
    }
    if skin is not None:
        doc["skins"] = [skin]
    if textures:
        doc["textures"] = textures
        doc["images"] = images
    if ganims:
        doc["animations"] = ganims

    json_bytes = _pad(json.dumps(doc, separators=(",", ":")).encode("utf-8"), b" ")
    bin_bytes = _pad(bytes(buf.data), b"\x00")
    total = 12 + 8 + len(json_bytes) + 8 + len(bin_bytes)
    return (struct.pack("<4sII", b"glTF", 2, total)
            + struct.pack("<I4s", len(json_bytes), b"JSON") + json_bytes
            + struct.pack("<I4s", len(bin_bytes), b"BIN\x00") + bin_bytes)


def write_glb(path, nodes, primitives, skin_joints, inverse_bind, animations, materials) -> None:
    data = build_glb(nodes, primitives, skin_joints, inverse_bind, animations, materials)
    with open(path, "wb") as f:
        f.write(data)


# ---------------------------------------------------------------- self-test

_SELFTEST_OUT = (r"C:\Users\arteo\AppData\Local\Temp\claude\G--workspace-projects-dream"
                 r"\a1124ce2-75c5-4c3b-9437-1fdb35f6d53b\scratchpad\gltftest\selftest.glb")

def _selftest() -> None:
    import io
    import os
    from PIL import Image

    out = _SELFTEST_OUT
    os.makedirs(os.path.dirname(out), exist_ok=True)

    nodes = [GltfNode("root", None), GltfNode("tip", 0, translation=(0.0, 1.0, 0.0))]
    corners = np.array([[x, y, z] for y in (0.0, 1.0) for x in (-0.5, 0.5) for z in (-0.5, 0.5)],
                       dtype=np.float32)
    # 12 triangles over the 8 corners (index = y*4 + x*2 + z)
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    tris = []
    for a, b, c, d in quads:
        tris += [(a, b, c), (a, c, d)]
    nrm = corners - np.array([0.0, 0.5, 0.0], dtype=np.float32)
    nrm /= np.linalg.norm(nrm, axis=1, keepdims=True)
    tip_w = corners[:, 1]
    weights = np.zeros((8, 4), dtype=np.float32)
    weights[:, 0] = 1.0 - tip_w
    weights[:, 1] = tip_w
    joints = np.zeros((8, 4), dtype=np.uint16)
    joints[:, 1] = 1
    prim = GltfPrimitive(corners, nrm.astype(np.float32),
                         corners[:, [0, 2]] + 0.5, np.array(tris, dtype=np.uint32),
                         joints, weights, 0)

    ibm = np.stack([np.eye(4, dtype=np.float32)] * 2)
    ibm[1][3, 1] = -1.0  # column-major translation (0,-1,0)

    s = np.sqrt(0.5)
    times = np.array([0.0, 0.5, 1.0], dtype=np.float32)
    anim = GltfAnimation(
        "idle",
        rotations={1: (times, np.array([[0, 0, 0, 1], [0, 0, s, s], [0, 0, 0, 1]], dtype=np.float32))},
        translations={0: (times, np.array([[0, 0, 0], [0, 0.1, 0], [0, 0, 0]], dtype=np.float32))})

    buf = io.BytesIO()
    Image.new("RGBA", (2, 2), (200, 60, 40, 255)).save(buf, format="PNG")
    mats = [{"name": "body", "base_color_factor": (1, 1, 1, 1), "base_color_png": buf.getvalue(),
             "metallic": 0.0, "roughness": 0.9}]

    args = (nodes, [prim], [0, 1], ibm, [anim], mats)
    write_glb(out, *args)
    with open(out, "rb") as f:
        first = f.read()
    write_glb(out, *args)
    with open(out, "rb") as f:
        second = f.read()
    assert first == second, "output is not deterministic"
    doc, boff = _check_container(first)
    assert "skins" in doc and "JOINTS_0" in doc["meshes"][0]["primitives"][0]["attributes"]
    pos = doc["accessors"][doc["meshes"][0]["primitives"][0]["attributes"]["POSITION"]]
    assert "min" in pos and "max" in pos
    assert len(doc["animations"][0]["samplers"]) == 2
    assert all(s["interpolation"] == "LINEAR" for s in doc["animations"][0]["samplers"])
    bv = doc["bufferViews"][doc["images"][0]["bufferView"]]
    assert first[boff + 8 + bv["byteOffset"]:][:8] == b"\x89PNG\r\n\x1a\n"
    print(f"PASS skinned selftest: {len(first)} bytes, {len(doc['accessors'])} accessors, "
          f"{len(doc['bufferViews'])} bufferViews -> {out}")


def _check_container(first: bytes):
    magic, version, length = struct.unpack_from("<4sII", first, 0)
    assert magic == b"glTF" and version == 2 and length == len(first)
    jlen, jtype = struct.unpack_from("<I4s", first, 12)
    assert jtype == b"JSON" and jlen % 4 == 0
    doc = json.loads(first[20:20 + jlen].decode("utf-8"))
    boff = 20 + jlen
    blen, btype = struct.unpack_from("<I4s", first, boff)
    assert btype == b"BIN\x00" and blen % 4 == 0
    assert boff + 8 + blen == len(first)
    used = doc["buffers"][0]["byteLength"]
    assert used <= blen < used + 4
    assert all(b == 0 for b in first[boff + 8 + used:])
    for bv in doc["bufferViews"]:
        assert bv["byteOffset"] % 4 == 0, bv
        assert bv["byteOffset"] + bv["byteLength"] <= blen
    for acc in doc["accessors"]:
        assert {"componentType", "count", "type"} <= acc.keys()
    return doc, boff


def _selftest_rigid() -> None:
    """Three nodes in a parent chain, one box mesh each, no skin, tangents on one."""
    import io
    import os
    from PIL import Image

    out = os.path.join(os.path.dirname(_SELFTEST_OUT), "selftest_rigid.glb")
    nodes = [GltfNode("torso", None),
             GltfNode("arm", 0, translation=(0.5, 0.8, 0.0)),
             GltfNode("hand", 1, translation=(0.0, -0.6, 0.0))]
    corners = np.array([[x, y, z] for y in (-0.2, 0.2) for x in (-0.2, 0.2) for z in (-0.2, 0.2)],
                       dtype=np.float32)
    quads = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    tris = np.array([t for a, b, c, d in quads for t in ((a, b, c), (a, c, d))], dtype=np.uint32)
    nrm = (corners / np.linalg.norm(corners, axis=1, keepdims=True)).astype(np.float32)
    tan = np.tile(np.array([1, 0, 0, 1], dtype=np.float32), (8, 1))
    prims = [GltfPrimitive(corners, nrm, corners[:, [0, 2]] + 0.5, tris, None, None, 0,
                           tangents=tan if i == 0 else None, node=i) for i in range(3)]
    s = np.sqrt(0.5)
    times = np.array([0.0, 0.4, 0.8], dtype=np.float32)
    swing = np.array([[0, 0, 0, 1], [0, 0, s, s], [0, 0, 0, 1]], dtype=np.float32)
    anim = GltfAnimation("idle", rotations={1: (times, swing), 2: (times, swing)})
    buf = io.BytesIO()
    Image.new("RGBA", (2, 2), (60, 120, 200, 255)).save(buf, format="PNG")
    mats = [{"name": "body", "base_color_factor": (1, 1, 1, 1), "base_color_png": buf.getvalue(),
             "metallic": 0.0, "roughness": 0.9}]
    args = (nodes, prims, None, None, [anim], mats)
    write_glb(out, *args)
    with open(out, "rb") as f:
        first = f.read()
    assert first == build_glb(*args), "output is not deterministic"
    doc, _ = _check_container(first)
    assert "skins" not in doc
    assert len(doc["meshes"]) == 3 and len(doc["nodes"]) == 3
    assert [n["mesh"] for n in doc["nodes"]] == [0, 1, 2]
    assert not any("skin" in n for n in doc["nodes"])
    for m in doc["meshes"]:
        attrs = m["primitives"][0]["attributes"]
        assert "JOINTS_0" not in attrs and "WEIGHTS_0" not in attrs
        assert "min" in doc["accessors"][attrs["POSITION"]]
    assert "TANGENT" in doc["meshes"][0]["primitives"][0]["attributes"]
    assert "TANGENT" not in doc["meshes"][1]["primitives"][0]["attributes"]
    assert doc["scenes"][0]["nodes"] == [0]
    chans = doc["animations"][0]["channels"]
    assert sorted((c["target"]["node"], c["target"]["path"]) for c in chans) == [
        (1, "rotation"), (2, "rotation")]
    print(f"PASS rigid selftest: {len(first)} bytes, {len(doc['accessors'])} accessors -> {out}")


if __name__ == "__main__":
    _selftest()
    _selftest_rigid()
