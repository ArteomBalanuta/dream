"""Regenerates every UI asset under dream/ui/. Usage: python build_all.py [out_root]"""
import importlib
import sys
from pathlib import Path

GENERATORS = ("gen_window", "gen_hud", "gen_items", "gen_fx")


def main() -> None:
    here = Path(__file__).resolve().parent
    sys.path.insert(0, str(here))
    out_root = Path(sys.argv[1]) if len(sys.argv) > 1 else here.parents[1] / "dream" / "ui"
    for name in GENERATORS:
        print(name)
        importlib.import_module(name).main(out_root)


if __name__ == "__main__":
    main()
