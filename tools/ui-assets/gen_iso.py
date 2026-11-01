"""Isometric sprite set: writes iso/sprites, iso/shadows, iso_manifest.json and iso_sheet.png.

  gen_iso.py [out_root]                       everything under out_root/iso (default: this directory)
  gen_iso.py --only name[,name] [out_root]    only those sprites and shadows, never the manifest or the sheet

Objects come from gen_iso_props, gen_iso_nature and gen_iso_relics (a missing module is skipped); each exposes
OBJECTS: dict[str, isoforge.ObjectSpec]. DREAM_ART_WORKERS sets the process count (default min(4, cpu)).
"""
from __future__ import annotations

import importlib
import json
import os
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import uiart as U  # noqa: E402

MODULES = ("gen_iso_props", "gen_iso_nature", "gen_iso_relics")
ORDER = ("rust_barrel", "dead_oak", "bramble", "slate_rocks", "tombstone", "bone_pile", "rune_potion_drop")
BG = (200, 200, 200)  # #C8C8C8
PAD = 20


def load_objects() -> dict:
    objs = {}
    for m in MODULES:
        try:
            mod = importlib.import_module(m)
        except ModuleNotFoundError as e:
            if e.name != m:
                raise
            continue
        objs.update(mod.OBJECTS)
    return {n: objs[n] for n in ORDER if n in objs} | {n: o for n, o in objs.items() if n not in ORDER}


def frame_names(name: str, spec) -> list[str]:
    return [name] if len(spec.frames) == 1 else [f"{name}_{i}" for i in range(len(spec.frames))]


def _job(job):
    """Worker: render one frame. Re-resolves the registry so only plain data crosses the process boundary."""
    name, fi = job
    r = load_objects()[name].frames[fi]()
    return r.rgba, r.shadow


def render_all(names: list[str]) -> dict:
    objs = load_objects()
    jobs = [(n, i) for n in names for i in range(len(objs[n].frames))]
    workers = int(os.environ.get("DREAM_ART_WORKERS", "0")) or min(4, os.cpu_count() or 1)
    workers = max(1, min(workers, len(jobs)))
    if workers == 1:
        res = [_job(j) for j in jobs]
    else:
        with ProcessPoolExecutor(max_workers=workers) as pool:
            res = list(pool.map(_job, jobs))
    return dict(zip(jobs, res))


def compose_sheet(entries: list[tuple]) -> np.ndarray:
    """entries: (rgba, shadow) per frame in order. Shadow then sprite over the neutral background, no labels."""
    rows, cur, curw = [], [], 0
    max_w = 1200
    for rgba, sh in entries:
        w = rgba.shape[1]
        if cur and curw + w + PAD > max_w:
            rows.append(cur)
            cur, curw = [], 0
        cur.append((rgba, sh))
        curw += w + PAD
    if cur:
        rows.append(cur)
    row_h = [max(e[0].shape[0] for e in r) for r in rows]
    width = max(sum(e[0].shape[1] + PAD for e in r) for r in rows) + PAD
    sheet = np.empty((sum(row_h) + PAD * (len(rows) + 1), width, 3), dtype=np.float64)
    sheet[:] = BG
    y = PAD
    for r, rh in zip(rows, row_h):
        x = PAD
        for rgba, sh in r:
            h, w = rgba.shape[:2]
            reg = sheet[y : y + h, x : x + w]
            sa = sh[..., 3:4] / 255.0
            reg[:] = reg * (1 - sa)
            a = rgba[..., 3:4] / 255.0
            reg[:] = reg * (1 - a) + rgba[..., :3] * a
            x += w + PAD
        y += rh + PAD
    return np.floor(sheet + 0.5).astype(np.uint8)


def main(out_root: Path = HERE, only: list[str] | None = None) -> None:
    out = Path(out_root) / "iso"
    objs = load_objects()
    if only:
        missing = [n for n in only if n not in objs]
        if missing:
            raise SystemExit(f"unknown objects: {missing}")
    names = [n for n in objs if not only or n in only]
    res = render_all(names)
    entries, manifest = [], []
    for n in names:
        spec = objs[n]
        frames = []
        for i, fname in enumerate(frame_names(n, spec)):
            rgba, shadow = res[(n, i)]
            if rgba.shape[:2] != (spec.size[1], spec.size[0]):
                raise SystemExit(f"{fname}: rendered {rgba.shape[1]}x{rgba.shape[0]}, spec says {spec.size}")
            U.save_png(rgba, out / "sprites" / f"{fname}.png")
            U.save_png(shadow, out / "shadows" / f"{fname}.png")
            entries.append((rgba, shadow))
            frames.append({"image": f"sprites/{fname}.png", "shadow": f"shadows/{fname}.png",
                           "durationMs": int(spec.duration_ms[i]) if spec.duration_ms else 0})
        manifest.append({"name": n, "size": list(spec.size), "pivot": list(spec.pivot), "loop": False, "frames": frames})
    if only:
        print("gen_iso: wrote", ", ".join(names))
        return
    doc = {"version": 1, "pitchDeg": 50, "yawDeg": 45, "pxPerUnit": 4, "yDown": True, "straightAlpha": True,
           "sheet": "iso_sheet.png", "objects": manifest}
    (out / "iso_manifest.json").write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    sheet = compose_sheet(entries)
    U.save_png(np.dstack((sheet, np.full(sheet.shape[:2], 255, dtype=np.uint8))), out / "iso_sheet.png")
    print("gen_iso: wrote", len(names), "objects,", len(entries), "frames")


if __name__ == "__main__":
    args = sys.argv[1:]
    only_names = None
    if args and args[0] == "--only":
        only_names = args[1].split(",")
        args = args[2:]
    main(Path(args[0]) if args else HERE, only_names)
