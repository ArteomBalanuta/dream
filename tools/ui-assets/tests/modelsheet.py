"""Render every model in the two equipment catalogues into one contact sheet, offline.

Why this exists: a capture puts the whole figure at 28x101 px and cycles the gallery from the client
clock, so you cannot ask it "show me the plate greaves". This reads the C# catalogues directly,
composes each model's parts out of the unit meshes the engine actually builds, and draws them at the
shipped camera angle. That makes every model inspectable by name, in one picture, in a second.

It is NOT the engine. There is no tint, no equipment pattern, no point light, no shadow, and the
gain is a single constant. What it answers is the geometry question - is this piece the shape I
meant, does it read as one object, is any part a floating slab - which is the question a gate cannot
answer and a gallery capture answers only by luck.

Feed it the OBJ dumps that EquipmentShapeLibraryTests writes:

    DREAM_SHAPE_DUMP=<dir> dotnet test dream.Tests/dream.Tests.csproj \
        --filter "FullyQualifiedName~EquipmentShapeDumpsToObj"
    python tools/ui-assets/tests/modelsheet.py <dir> <out.png> [armour|weapons]
"""
import math
import os
import re
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
ARMOUR = os.path.join(REPO, "dream", "Rendering", "Actors", "ArmourMeshCatalog.cs")
WEAPONS = os.path.join(REPO, "dream", "Rendering", "Actors", "WeaponMeshCatalog.cs")

PITCH = math.radians(50.0)
YAW = math.radians(45.0)
LIGHT = np.array([0.45, 0.80, 0.40])
LIGHT /= np.linalg.norm(LIGHT)
GAIN = 2.0  # the world pass has no tonemap; this stands in for a mid-day gain


def read_palette(path, enum_name):
    src = open(path, encoding="utf-8-sig").read()
    block = src[src.index(f"BaseColor({enum_name} material)"):]
    block = block[:block.index("};")]
    out = {}
    for m in re.finditer(rf"{enum_name}\.(\w+)\s*=>\s*new Vector4\(([-\d.f, ]+)\)", block):
        nums = [float(x.strip().rstrip("f")) for x in m.group(2).split(",")]
        out[m.group(1)] = np.array(nums[:3])
    return out


def read_models(path, enum_name):
    """Every Add/Hand/Shield/... block, as (visual id, [(centre, size, material, kw), ...])."""
    src = open(path, encoding="utf-8-sig").read()
    models = []
    for m in re.finditer(r'\b\w+\(\s*"((?:worn|held)\.[a-z]+\.[a-z0-9-]+)"(.*?)\);\s*\n', src, re.S):
        vid, body = m.group(1), m.group(2)
        parts = []
        for call in re.finditer(r"Box\(([^()]*?)\)(?=,\s*\n|\s*$)", body, re.S):
            toks = [t.strip() for t in call.group(1).split(",")]
            try:
                nums = [float(t.rstrip("f")) for t in toks[:6]]
            except ValueError:
                continue
            mat = toks[6].split(".")[-1]
            kw = {"pitch": 0.0, "roll": 0.0, "yaw": 0.0, "shape": "Box"}
            for t in toks[7:]:
                if ":" not in t:
                    continue
                k, v = [x.strip() for x in t.split(":", 1)]
                kw[k] = v.split(".")[-1] if k == "shape" else float(v.rstrip("f"))
            parts.append((np.array(nums[:3]), np.array(nums[3:6]), mat, kw))
        if parts:
            models.append((vid, parts))
    return models


def load_obj(path):
    pos, nrm, faces = [], [], []
    for line in open(path):
        t = line.split()
        if not t:
            continue
        if t[0] == "v":
            pos.append([float(x) for x in t[1:4]])
        elif t[0] == "vn":
            nrm.append([float(x) for x in t[1:4]])
        elif t[0] == "f":
            faces.append([int(x.split("/")[0]) - 1 for x in t[1:4]])
    return np.array(pos), np.array(nrm), np.array(faces, dtype=np.int64)


def rot_x(d):
    a = math.radians(d)
    return np.array([[1, 0, 0], [0, math.cos(a), math.sin(a)], [0, -math.sin(a), math.cos(a)]])


def rot_y(d):
    a = math.radians(d)
    return np.array([[math.cos(a), 0, -math.sin(a)], [0, 1, 0], [math.sin(a), 0, math.cos(a)]])


def rot_z(d):
    a = math.radians(d)
    return np.array([[math.cos(a), math.sin(a), 0], [-math.sin(a), math.cos(a), 0], [0, 0, 1]])


def view():
    cy, sy = math.cos(YAW), math.sin(YAW)
    cp, sp = math.cos(PITCH), math.sin(PITCH)
    return np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]]) @ np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])


def compose(parts, shapes, palette):
    """World-space triangles with their vertex normals and albedo."""
    tris = []
    for centre, size, mat, kw in parts:
        name = kw["shape"]
        if name not in shapes:
            continue
        pos, nrm, faces = shapes[name]
        # The part matrix: scale, then pitch (X), roll (Z), yaw (Y), then translate.
        m = np.diag(size) @ rot_x(kw["pitch"]) @ rot_z(kw["roll"]) @ rot_y(kw["yaw"])
        world = pos @ m + centre
        # Normals take the inverse transpose; the scale is diagonal, so dividing is enough.
        nm = np.diag(1.0 / np.maximum(size, 1e-6)) @ rot_x(kw["pitch"]) @ rot_z(kw["roll"]) @ rot_y(kw["yaw"])
        wn = nrm @ nm
        wn /= np.maximum(np.linalg.norm(wn, axis=-1, keepdims=True), 1e-9)
        colour = palette.get(mat, np.array([0.3, 0.3, 0.3]))
        for f in faces:
            tris.append((world[f], wn[f], colour))
    return tris


def render(tris, size):
    m = view()
    img = np.zeros((size, size, 3))
    depth = np.full((size, size), -1e9)
    if not tris:
        return (img * 255).astype(np.uint8)

    allv = np.concatenate([t[0] for t in tris]) @ m.T
    lo, hi = allv[:, :2].min(axis=0), allv[:, :2].max(axis=0)
    span = max((hi - lo).max(), 1e-6)
    scale = (size * 0.86) / span
    centre = (hi + lo) * 0.5
    eye = np.array([0.0, 0.0, 1.0])
    hv = LIGHT + eye
    hv /= np.linalg.norm(hv)

    for world, normals, colour in tris:
        vp = world @ m.T
        vn = normals @ m.T
        x = (vp[:, 0] - centre[0]) * scale + size * 0.5
        y = size * 0.5 - (vp[:, 1] - centre[1]) * scale
        area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if area >= 0:
            continue
        x0, x1 = max(int(x.min()), 0), min(int(x.max()) + 1, size)
        y0, y1 = max(int(y.min()), 0), min(int(y.max()) + 1, size)
        if x0 >= x1 or y0 >= y1:
            continue
        px, py = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        w0 = ((x[1] - x[0]) * (py - y[0]) - (px - x[0]) * (y[1] - y[0])) / area
        w1 = ((x[2] - x[1]) * (py - y[1]) - (px - x[1]) * (y[2] - y[1])) / area
        w2 = 1.0 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        ba, bb, bc = w1, w2, w0
        z = ba * vp[0, 2] + bb * vp[1, 2] + bc * vp[2, 2]
        n = ba[..., None] * vn[0] + bb[..., None] * vn[1] + bc[..., None] * vn[2]
        n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-9)
        ndl = np.clip(n @ LIGHT, 0.0, 1.0)
        spec = np.clip(n @ hv, 0.0, 1.0) ** 22.0
        shade = (0.22 + 0.78 * ndl) * GAIN
        col = np.clip(colour * shade[..., None] + 0.22 * spec[..., None], 0, 1)
        sub = depth[y0:y1, x0:x1]
        win = inside & (z > sub)
        sub[win] = z[win]
        img[y0:y1, x0:x1][win] = col[win]
    return (img * 255).astype(np.uint8)


def main():
    dump = sys.argv[1]
    out = sys.argv[2]
    which = sys.argv[3] if len(sys.argv) > 3 else "all"

    shapes = {n[:-4]: load_obj(os.path.join(dump, n)) for n in os.listdir(dump) if n.endswith(".obj")}
    catalogues = []
    if which in ("all", "armour"):
        catalogues.append((ARMOUR, "ArmourMaterial"))
    if which in ("all", "weapons"):
        catalogues.append((WEAPONS, "WeaponMaterial"))

    entries = []
    for path, enum_name in catalogues:
        palette = read_palette(path, enum_name)
        for vid, parts in read_models(path, enum_name):
            entries.append((vid, compose(parts, shapes, palette), len(parts)))

    tile, ss, label, cols = 190, 2, 20, 5
    rows = (len(entries) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tile, rows * (tile + label)), (12, 12, 16))
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("arial.ttf", 12)
    except OSError:
        font = ImageFont.load_default()

    for i, (vid, tris, count) in enumerate(entries):
        im = Image.fromarray(render(tris, tile * ss)).resize((tile, tile), Image.LANCZOS)
        cx, cy = (i % cols) * tile, (i // cols) * (tile + label)
        sheet.paste(im, (cx, cy))
        draw.text((cx + 4, cy + tile + 3), f"{vid.split('.', 1)[1]}  [{count}]", fill=(185, 182, 200), font=font)

    sheet.save(out)
    print("wrote", out, sheet.size, len(entries), "models")


if __name__ == "__main__":
    main()
