"""Render every dumped unit shape from the game's isometric angle into one contact sheet.

Not a substitute for an in-engine capture: no tint, no grain, no point lights. What it is for is
seeing the FORM of a primitive - whether a pauldron swells and tucks, whether a membrane's ribs
break the value - at roughly the size the shipped zoom gives a part.
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

SRC = sys.argv[1]
OUT = sys.argv[2]
TILE = int(sys.argv[3]) if len(sys.argv) > 3 else 176
SS = 3  # supersample

# The shipped isometric camera: pitch 50 deg down, yaw 45 deg.
PITCH = math.radians(50.0)
YAW = math.radians(45.0)
LIGHT = np.array([0.45, 0.80, 0.40])
LIGHT /= np.linalg.norm(LIGHT)


def load_obj(path):
    pos, nrm, faces = [], [], []
    with open(path) as fh:
        for line in fh:
            if line.startswith("v "):
                pos.append([float(x) for x in line.split()[1:4]])
            elif line.startswith("vn "):
                nrm.append([float(x) for x in line.split()[1:4]])
            elif line.startswith("f "):
                faces.append([int(t.split("/")[0]) - 1 for t in line.split()[1:4]])
    return np.array(pos), np.array(nrm), np.array(faces, dtype=np.int64)


def view_matrix():
    cy, sy = math.cos(YAW), math.sin(YAW)
    cp, sp = math.cos(PITCH), math.sin(PITCH)
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rx = np.array([[1, 0, 0], [0, cp, -sp], [0, sp, cp]])
    return rx @ ry


def render(path, size):
    pos, nrm, faces = load_obj(path)
    m = view_matrix()
    vp = pos @ m.T
    vn = nrm @ m.T

    # Fit the projected silhouette into the tile with a margin.
    lo = vp[:, :2].min(axis=0)
    hi = vp[:, :2].max(axis=0)
    span = max((hi - lo).max(), 1e-6)
    scale = (size * 0.84) / span
    centre = (hi + lo) * 0.5

    img = np.zeros((size, size, 3), dtype=np.float64)
    depth = np.full((size, size), -1e9)

    sx = (vp[:, 0] - centre[0]) * scale + size * 0.5
    sy = size * 0.5 - (vp[:, 1] - centre[1]) * scale
    sz = vp[:, 2]

    for f in faces:
        a, b, c = f
        x = np.array([sx[a], sx[b], sx[c]])
        y = np.array([sy[a], sy[b], sy[c]])
        area = (x[1] - x[0]) * (y[2] - y[0]) - (x[2] - x[0]) * (y[1] - y[0])
        if area >= 0:  # back-facing after the y flip
            continue
        x0 = max(int(math.floor(x.min())), 0)
        x1 = min(int(math.ceil(x.max())) + 1, size)
        y0 = max(int(math.floor(y.min())), 0)
        y1 = min(int(math.ceil(y.max())) + 1, size)
        if x0 >= x1 or y0 >= y1:
            continue
        px, py = np.meshgrid(np.arange(x0, x1) + 0.5, np.arange(y0, y1) + 0.5)
        w0 = ((x[1] - x[0]) * (py - y[0]) - (px - x[0]) * (y[1] - y[0])) / area
        w1 = ((x[2] - x[1]) * (py - y[1]) - (px - x[1]) * (y[2] - y[1])) / area
        w2 = 1.0 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any():
            continue
        # barycentric order: w2 -> a, w1 -> b? keep it simple and consistent
        ba, bb, bc = w1, w2, w0
        z = ba * sz[a] + bb * sz[b] + bc * sz[c]
        n = (ba[..., None] * vn[a] + bb[..., None] * vn[b] + bc[..., None] * vn[c])
        n /= np.maximum(np.linalg.norm(n, axis=-1, keepdims=True), 1e-9)
        ndl = np.clip(n @ LIGHT, 0.0, 1.0)
        view = np.array([0.0, 0.0, 1.0])
        hv = LIGHT + view
        hv /= np.linalg.norm(hv)
        spec = np.clip(n @ hv, 0.0, 1.0) ** 26.0
        shade = 0.16 + 0.70 * ndl + 0.42 * spec
        col = np.clip(np.stack([shade * 0.80, shade * 0.83, shade * 0.90], axis=-1), 0, 1)

        sub = depth[y0:y1, x0:x1]
        win = inside & (z > sub)
        sub[win] = z[win]
        tgt = img[y0:y1, x0:x1]
        tgt[win] = col[win]
    return (img * 255).astype(np.uint8)


names = sorted(n[:-4] for n in os.listdir(SRC) if n.endswith(".obj"))
cols = 5
rows = (len(names) + cols - 1) // cols
label = 18
sheet = Image.new("RGB", (cols * TILE, rows * (TILE + label)), (14, 13, 20))
draw = ImageDraw.Draw(sheet)
try:
    font = ImageFont.truetype("arial.ttf", 13)
except OSError:
    font = ImageFont.load_default()

for i, name in enumerate(names):
    tile = render(os.path.join(SRC, name + ".obj"), TILE * SS)
    im = Image.fromarray(tile).resize((TILE, TILE), Image.LANCZOS)
    cx = (i % cols) * TILE
    cy = (i // cols) * (TILE + label)
    sheet.paste(im, (cx, cy))
    draw.text((cx + 5, cy + TILE + 2), name, fill=(190, 185, 205), font=font)

sheet.save(OUT)
print("wrote", OUT, sheet.size, len(names), "shapes")
