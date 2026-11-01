"""Independent manifest check: parses the Manifest tables in 07-asset-plan.md itself
(not the generators), expands the shorthand, and compares with the committed atlases.

Exits nonzero on missing sprites, wrong sizes or extra sprites.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
_REL = Path("docs") / "superpowers" / "specs" / "2026-11-01-classic-mmo-ui" / "07-asset-plan.md"
# The plan may live only in the main checkout when run from a worktree under .claude/worktrees.
PLAN = next((p / _REL for p in [ROOT, *ROOT.parents] if (p / _REL).exists()), ROOT / _REL)
UI = ROOT / "dream" / "ui"
SUFFIX = {"n": "normal", "h": "hover", "p": "pressed", "d": "disabled"}
SIZE_RE = re.compile(r"(\d+)x(\d+)")


def split_groups(cell: str) -> list[str]:
    """Split on ' / ' that sits outside backticks."""
    out, cur, inside = [], "", False
    i = 0
    while i < len(cell):
        ch = cell[i]
        if ch == "`":
            inside = not inside
        if not inside and cell[i : i + 3] == " / ":
            out.append(cur)
            cur = ""
            i += 3
            continue
        cur += ch
        i += 1
    out.append(cur)
    return [g.strip() for g in out]


def expand_group(group: str, prev_full: str | None, default_suffixes: list[str] | None = None):
    """Expand one group like '`a.b` `c.d` `{n,h}`' or '`a.left/right`' into names."""
    toks = re.findall(r"`([^`]*)`", group)
    names: list[str] = []
    suffixes: list[str] | None = default_suffixes
    for t in toks:
        m = re.fullmatch(r"\{([a-z,]+)\}", t)
        if m:
            suffixes = [SUFFIX[c] for c in m.group(1).split(",")]
            continue
        if t.startswith("."):
            # '.w' after ' / ' replaces the last segment of the previous name;
            # '.off .on' after a bare name lists suffixes of that name.
            for part in t.split():
                if prev_full is None:
                    raise ValueError(f"dangling {t}")
                names.append(prev_full.rsplit(".", 1)[0] + part)
            continue
        if "/" in t:
            stem, _, last = t.rpartition(".")
            alts = last.split("/")
            # the first alt may be preceded by the real stem; stem has no '/'
            for a in alts:
                names.append(f"{stem}.{a}")
            continue
        names.append(t)
    base = names[0] if names else prev_full
    if suffixes:
        names = [f"{n}.{s}" for n in names for s in suffixes]
    return names, base


def parse_cell_names(cell: str):
    """Returns (list of name-lists per ' / ' group, flag base_is_suffix_parent)."""
    groups = split_groups(cell)
    # '`a` / `.b` / `.c` `{n,h}`': the suffix list at the end covers every group in the cell
    m = re.search(r"`\{([a-z,]+)\}`", cell)
    shared = [SUFFIX[c] for c in m.group(1).split(",")] if m else None
    res = []
    prev = None
    for g in groups:
        toks = re.findall(r"`([^`]*)`", g)
        # '`hud.ministat` `.off .on`': suffix list on a bare name, base itself is not a sprite
        if len(toks) == 2 and toks[1].startswith(".") and len(groups) == 1:
            res.append([toks[0] + p for p in toks[1].split()])
            prev = res[-1][-1]
            continue
        names, prev = expand_group(g, prev, shared)
        res.append(names)
    return res


def parse_plan() -> dict[str, dict[str, tuple[int, int]]]:
    """atlas key -> {sprite: (w_px, h_px)}"""
    text = PLAN.read_text(encoding="utf-8")
    expected: dict[str, dict[str, tuple[int, int]]] = {"window": {}, "hud": {}, "items": {}}
    section = None
    for line in text.splitlines():
        if line.startswith("### A."):
            section = "window"
        elif line.startswith("### B."):
            section = "hud"
        elif line.startswith("### C."):
            section = "items"
        elif line.startswith("## ") and not line.startswith("## Manifest"):
            section = None
        if section is None or not line.startswith("|"):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if section in ("window", "hud"):
            if cells[0] in ("Sprite",) or set(cells[0]) <= {"-"}:
                continue
            groups = parse_cell_names(cells[0])
            sizes = [(int(a), int(b)) for a, b in SIZE_RE.findall(cells[1])]
            if len(sizes) == 1:
                sizes = sizes * len(groups)
            if len(sizes) != len(groups):
                raise ValueError(f"size/name mismatch in row: {cells[0]} | {cells[1]}")
            for names, (w, h) in zip(groups, sizes):
                for n in names:
                    expected[section][n] = (w * 4, h * 4)
        else:
            if not cells[0].startswith(("1x1", "1x2", "2x2", "2x3", "2x4", "3x2")):
                continue
            w, h = (int(v) for v in SIZE_RE.search(cells[1]).groups())
            for n in re.findall(r"`([^`]+)`", cells[2]):
                expected["items"][n] = (w * 4, h * 4)
    expected["items"]["fx.aura.blob"] = (120, 120)
    # cursors: prose line in section B, 'cursor.pointer .pressed .grab .attack .forbid', 32x32 u
    m = re.search(r"`(cursor\.\w+)((?: \.\w+)+)`, (\d+)x(\d+) u", text)
    names = [m.group(1)] + [f"cursor{p}" for p in m.group(2).split()]
    cursors = {n: (int(m.group(3)) * 4, int(m.group(4)) * 4) for n in names}
    return expected, cursors


def load(path: Path):
    doc = json.loads(path.read_text(encoding="utf-8"))
    return {k: (v["w"], v["h"]) for k, v in doc["sprites"].items()}


def main() -> int:
    expected, cursors = parse_plan()
    errs: list[str] = []
    actual_sets = {
        "window": load(UI / "window" / "window.json"),
        "hud": load(UI / "hud" / "hud.json"),
        "items": load(UI / "items" / "items.json"),
        "cursors": load(UI / "hud" / "cursors.json"),
    }
    expected["cursors"] = cursors
    # sanity on the parser itself: counts the plan implies
    for key, n in (("window", 54), ("hud", 80), ("items", 28), ("cursors", 5)):
        if len(expected[key]) != n:
            errs.append(f"parser produced {len(expected[key])} {key} sprites, hand count is {n}")
    for key, exp in expected.items():
        act = actual_sets[key]
        for name, size in sorted(exp.items()):
            if name not in act:
                errs.append(f"{key}: missing {name}")
            elif act[name] != size:
                errs.append(f"{key}: {name} is {act[name]} px, plan says {size}")
        for name in sorted(set(act) - set(exp)):
            errs.append(f"{key}: extra sprite {name}")

    # combat text: texel cells and x4 sizes
    d1 = load(UI / "fx" / "dmg_digits.json")
    d4 = load(UI / "fx" / "dmg_digits_x4.json")
    for n in [f"dmg.{i}" for i in range(10)] + ["dmg.plus", "dmg.comma"]:
        if d1.get(n) != (12, 16):
            errs.append(f"dmg_digits: {n} is {d1.get(n)}, plan says (12, 16)")
        if d4.get(n) != (48, 64):
            errs.append(f"dmg_digits_x4: {n} is {d4.get(n)}, plan says (48, 64)")
    if d4.get("dmg.miss") != (128, 44):
        errs.append(f"dmg_digits_x4: dmg.miss is {d4.get('dmg.miss')}, plan says (128, 44)")
    for n in ("dmg.miss.M", "dmg.miss.i", "dmg.miss.s"):
        if n not in d4:
            errs.append(f"dmg_digits_x4: missing {n}")
    allowed = {f"dmg.{i}" for i in range(10)} | {"dmg.plus", "dmg.comma"}
    for n in sorted(set(d1) - allowed):
        errs.append(f"dmg_digits: extra {n}")
    for n in sorted(set(d4) - allowed - {"dmg.miss", "dmg.miss.M", "dmg.miss.i", "dmg.miss.s"}):
        errs.append(f"dmg_digits_x4: extra {n}")

    total = sum(len(v) for v in expected.values())
    for e in errs:
        print("FAIL", e)
    print(f"manifest: {total} planned sprites checked, {len(errs)} problems")
    return 1 if errs else 0


if __name__ == "__main__":
    sys.exit(main())
