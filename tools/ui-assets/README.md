# UI art generators

Python scripts that generate dream's UI art into `dream/ui/`. All art is original and procedural, with fixed seeds. Nothing is wired into the game yet.

```bash
python -m venv .venv
.venv/Scripts/python -m pip install -r tools/ui-assets/requirements.txt
.venv/Scripts/python tools/ui-assets/build_all.py
```

Each `gen_*.py` exposes `main(out_root)` and writes only its own directory. Each `tests/check_*.py` exits nonzero on failure.

Atlases are straight-alpha RGBA PNGs at 4 px per logical unit with a JSON sprite table. Rects exclude the extrusion. `yDown` is true, so the loader must not flip V. The spec lives in `docs/superpowers/specs/2026-11-01-classic-mmo-ui/`.

`items_mask.png` is a data texture: RGB stays meaningful where A=0, so load it raw, with no premultiply and no alpha stripping.
