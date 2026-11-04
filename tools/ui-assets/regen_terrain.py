"""Regenerates the baked terrain tiles and ships them: terrain/ (tiles, manifest, sheet) plus the engine copies at
dream/animations/library/forge/<n>/<n>.albedo.png, byte for byte the same files.

  regen_terrain.py [--only a,b] [--decals]

Prints the measured TILE_LUMA and TILE_RGB (from the in-memory tiles, no file reads) to paste into gen_ground_decals.py.
--decals also re-runs gen_ground_decals.main(), which darkens the decals through TILE_LUMA; paste the numbers first.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
if str(HERE) not in sys.path:
    sys.path.insert(0, str(HERE))
import gen_terrain  # noqa: E402

LUMA = np.array([0.2126, 0.7152, 0.0722])
RGB_KEYS = ("dirt", "grass_top", "grass_path", "mud", "stone", "sand", "rock")   # gen_ground_decals.TILE_RGB


def main(argv: list[str]) -> int:
    only = None
    if "--only" in argv:
        i = argv.index("--only")
        if i + 1 >= len(argv) or argv[i + 1].startswith("--"):
            print("usage: regen_terrain.py [--only a,b] [--decals]   (--only needs a comma separated tile list)", file=sys.stderr)
            return 2
        only = argv[i + 1].split(",")
    res: dict = {}
    gen_terrain.main(HERE, only, ship_root=ROOT / "dream" / "animations" / "library" / "forge", results=res)
    luma = {n: float((t[..., :3].astype(np.float64) / 255.0 @ LUMA).mean()) for n, t in sorted(res.items())}
    print("TILE_LUMA = {" + ", ".join(f'"{n}": {v:.4f}' for n, v in luma.items()) + "}")
    rgb = {n: tuple(int(round(c)) for c in res[n][..., :3].reshape(-1, 3).mean(axis=0)) for n in RGB_KEYS if n in res}
    print("TILE_RGB = {" + ", ".join(f'"{n}": {v}' for n, v in rgb.items()) + "}")
    if "--decals" in argv:
        import gen_ground_decals  # noqa: E402
        gen_ground_decals.main(with_preview=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
