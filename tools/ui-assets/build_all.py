"""Regenerates every generated asset under dream/. Usage: python build_all.py [ui_out_root]

Two families, because they do not share an output root:

  * the UI sprite sheets, which all take one out_root and write under it (dream/ui/ by default);
  * the forged 3D actors and props, which each write into the directory the engine loads them from
    (dream/animations/...). Those are listed with an explicit call, because they differ: the player ships
    under the one filename PlayerModelAssetDefinition asks for, the villagers write one GLB per variant.

dream/animations/enemies/wolf.glb is NOT generated: it is the third-party pack the hound is replacing, and
gen_char_beast writes hound.glb beside it without touching it.
"""
import importlib
import sys
from pathlib import Path

UI_GENERATORS = ("gen_window", "gen_hud", "gen_items", "gen_fx", "gen_particles", "gen_menu")

ASSET_GENERATORS = (
    ("gen_char_player", lambda m, root: m.main(str(root / "animations" / "players"), "player-grim.glb")),
    ("gen_char_npc", lambda m, root: m.main(str(root / "animations" / "npcs"))),
    ("gen_char_beast", lambda m, root: m.main(str(root / "animations" / "enemies"))),
    ("gen_props", lambda m, root: m.main(str(root / "animations" / "props"))),
)


def main() -> None:
    here = Path(__file__).resolve().parent
    sys.path.insert(0, str(here))
    out_root = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parents[1] / "dream" / "ui"
    for name in UI_GENERATORS:
        print(name)
        importlib.import_module(name).main(out_root)
    # The asset root is dream/, i.e. the ui root's parent when it is the default dream/ui.
    asset_root = out_root.parent if out_root.name == "ui" else out_root
    for name, run in ASSET_GENERATORS:
        print(name)
        run(importlib.import_module(name), asset_root)


if __name__ == "__main__":
    main()
