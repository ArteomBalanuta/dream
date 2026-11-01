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
GAP = 28
SHEET_COL_W = 1040  # the column beside the tallest object


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


def _wrap(items: list, max_w: int) -> list:
    """Greedy rows of (rgba, shadow) items no wider than max_w, GAP between neighbours."""
    rows, cur, curw = [], [], 0
    for it in items:
        w = it[0].shape[1]
        if cur and curw + GAP + w > max_w:
            rows.append(cur)
            cur, curw = [], 0
        curw += w + (GAP if cur else 0)
        cur.append(it)
    return rows + [cur] if cur else rows


def compose_sheet(entries: list[tuple], groups: list | None = None) -> np.ndarray:
    """entries: (rgba, shadow) per frame in order; groups: an object id per entry, default every entry on its own.

    Shadow then sprite over the neutral background, no labels. Each object is trimmed to what it draws, and the frames
    of an animation share one crop so the motion stays in register. The tallest still object stands at the left, the
    other stills fill the rows beside it and animations get the last row in frame order, all bottom aligned and justified.
    """
    groups = list(range(len(entries))) if groups is None else groups
    members = {g: [e for e, k in zip(entries, groups) if k == g] for g in groups}
    items = {}
    for g, frames in members.items():
        cover = np.any([(r[..., 3] > 0) | (s[..., 3] > 0) for r, s in frames], axis=0)
        ys, xs = np.flatnonzero(cover.any(axis=1)), np.flatnonzero(cover.any(axis=0))
        items[g] = [(r[ys[0] : ys[-1] + 1, xs[0] : xs[-1] + 1], s[ys[0] : ys[-1] + 1, xs[0] : xs[-1] + 1]) for r, s in frames]
    stills = [v[0] for v in items.values() if len(v) == 1]
    frames = [f for v in items.values() if len(v) > 1 for f in v]
    hero = max(stills, key=lambda e: e[0].shape[0]) if stills else None
    stills = [e for e in stills if e is not hero]
    rows = _wrap(stills, SHEET_COL_W) + _wrap(frames, SHEET_COL_W)
    col_w = max((sum(e[0].shape[1] for e in r) + GAP * (len(r) - 1) for r in rows), default=0)
    row_h = [max(e[0].shape[0] for e in r) for r in rows]
    col_h = sum(row_h) + GAP * max(len(rows) - 1, 0)
    hero_w, hero_h = (hero[0].shape[1], hero[0].shape[0]) if hero else (0, 0)
    height = max(hero_h, col_h)
    col_x = PAD + hero_w + (GAP if hero_w and rows else 0)
    sheet = np.empty((height + 2 * PAD, col_x + col_w + PAD, 3), dtype=np.float64)
    sheet[:] = BG

    def stamp(rgba, sh, x, y):
        h, w = rgba.shape[:2]
        reg = sheet[y : y + h, x : x + w]
        reg[:] = reg * (1 - sh[..., 3:4] / 255.0)
        a = rgba[..., 3:4] / 255.0
        reg[:] = reg * (1 - a) + rgba[..., :3] * a

    if hero:
        stamp(hero[0], hero[1], PAD, PAD + height - hero_h)
    y = PAD + height - col_h
    for r, rh in zip(rows, row_h):
        spare = col_w - sum(e[0].shape[1] for e in r)
        for i, (rgba, sh) in enumerate(r):
            x = col_x + sum(e[0].shape[1] for e in r[:i]) + (spare * i // (len(r) - 1) if len(r) > 1 else 0)
            stamp(rgba, sh, x, y + rh - rgba.shape[0])
        y += rh + GAP
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
    entries, groups, manifest = [], [], []
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
            groups.append(n)
            frames.append({"image": f"sprites/{fname}.png", "shadow": f"shadows/{fname}.png",
                           "durationMs": int(spec.duration_ms[i]) if spec.duration_ms else 0})
        manifest.append({"name": n, "size": list(spec.size), "pivot": list(spec.pivot), "loop": False, "frames": frames})
    if only:
        print("gen_iso: wrote", ", ".join(names))
        return
    doc = {"version": 1, "pitchDeg": 50, "yawDeg": 45, "pxPerUnit": 4, "yDown": True, "straightAlpha": True,
           "sheet": "iso_sheet.png", "objects": manifest}
    (out / "iso_manifest.json").write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8", newline="\n")
    sheet = compose_sheet(entries, groups)
    U.save_png(np.dstack((sheet, np.full(sheet.shape[:2], 255, dtype=np.uint8))), out / "iso_sheet.png")
    print("gen_iso: wrote", len(names), "objects,", len(entries), "frames")


if __name__ == "__main__":
    args = sys.argv[1:]
    only_names = None
    if args and args[0] == "--only":
        only_names = args[1].split(",")
        args = args[2:]
    main(Path(args[0]) if args else HERE, only_names)
