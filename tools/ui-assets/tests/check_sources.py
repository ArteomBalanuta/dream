"""Generator hygiene: no reads of reference images, fonts or network; no source-game names."""
import ast
import io
import json
import re
import sys
import tokenize
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
TOOLS = ROOT / "tools" / "ui-assets"
UI = ROOT / "dream" / "ui"
ALLOWED_MODULES = {"__future__", "json", "math", "sys", "pathlib", "numpy", "PIL", "uiart", "typing",
                   "dataclasses", "re", "hashlib", "tempfile", "importlib", "zlib",
                   # forge.py (shared renderer), gemtrace.py (gem ray tracer) and its process pool over tiles
                   "forge", "gemtrace", "concurrent", "os",
                   # isoforge.py (isometric renderer) and the gen_iso*.py generators that build on it
                   "isoforge",
                   # gen_menu.py reuses gen_window.py's frame builders
                   "gen_window",
                   # charforge.py / gltfwrite.py (actor rig, skinning and the GLB writer) and the
                   # gen_char_*.py generators over them; struct and io pack the binary glTF chunks
                   "charforge", "gltfwrite", "gen_char_player", "struct", "io",
                   # WIDENING, not a relaxation: these are new sibling generator modules of the same
                   # kind as gen_char_player (enginelook.py states the engine's actor transfer function,
                   # gen_char_npc.py and gen_char_beast.py forge the villagers and the hound). The rule
                   # is unchanged - a tool may import its own siblings and the listed stdlib, nothing
                   # that reads art the project did not generate. Adding a sibling here needs the
                   # sibling to be an original generator in this directory; nothing else moves.
                   "enginelook", "gen_char_npc", "gen_char_beast",
                   # gen_terrain (baked surface tiles) and gen_ground_decals (decals that re-darken through the
                   # tiles' luma) are original sibling generators; regen_terrain.py imports them to rebuild both.
                   "gen_terrain", "gen_ground_decals"}
BAD_STR = re.compile(r"reference|\.webp|fonts|\.ttf|\.otf|https?:|screenshot", re.I)
# Every file read in a generator must be of generated output or of a sibling tool file. This is THE place a read
# of art the project did not generate would have to be caught, so it is an ERROR with a named allow-list. It used
# to `print` a note and append nothing to errs, which meant no read in any generator could fail the gate while the
# comment above it said "must": a comment that overstates a gate is how the next reader trusts something that is
# not there.
#
# uiart.py is exempt, deliberately and narrowly: it is the shared toolkit, and this chain's file I/O lives there on
# purpose (save_png, the atlas validators, sprite_pixels, the shipped-bytes gates, tokens.json). All twelve of its
# reads are of a generated atlas, a temp render or its own tokens.json. Keeping the rule on the generators is
# keeping it where a reference image would actually be reached for.
READ_EXEMPT_FILES = {"uiart.py"}
# (file, the exact source of the call) -> why this read is legitimate. Anything else is a failure. Keyed on the
# source text and not the line number, so inserting a line above a read cannot silently re-classify it.
READ_ALLOW = {
    ("gen_char_player.py", "path.read_bytes()"):
        "re-reads the GLB it has just written, to print its size and sha1",
    ("gen_items.py", '(items / "items.json").read_text(encoding="utf-8")'):
        "preview(): reads back the atlas json it has just written",
    ("gen_items.py", "Image.open(items / image)"):
        "preview(): reads back the variant sheets it has just written",
    ("gen_particles.py", 'Image.open(Path(out_root) / "particles" / "particles.png")'):
        "preview(): reads back the sheet it has just written",
}


def read_errors(name: str, src: str) -> tuple[list[str], set]:
    """Classifies every file read in one tool file. Returns (failures, the allow-list entries it matched)."""
    errs, seen = [], set()
    if name in READ_EXEMPT_FILES:
        return errs, seen
    for n in ast.walk(ast.parse(src)):
        if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute) and n.func.attr in ("open", "read_bytes", "read_text"):
            seg = " ".join((ast.get_source_segment(src, n) or "").split())
            if (name, seg) in READ_ALLOW:
                seen.add((name, seg))
            else:
                errs.append(f"{name}:{n.lineno}: reads {seg[:70]!r} - not a classified read of generated output or "
                            f"a sibling tool file (add it to READ_ALLOW with the reason, or stop reading it)")
    return errs, seen


BAD_NAME = re.compile(r"(^|[._\-])(mu|muonline|zen|chaos|larkan|loch|arcana|soul|bless|webzen|kundun|lorencia)([._\-]|$)", re.I)
errs: list[str] = []
seen_reads: set = set()

for p in sorted(TOOLS.glob("*.py")):
    src = p.read_text(encoding="utf-8")
    tree = ast.parse(src)
    read_errs, matched = read_errors(p.name, src)
    errs += read_errs
    seen_reads |= matched
    for n in ast.walk(tree):
        mods = []
        if isinstance(n, ast.Import):
            mods = [a.name for a in n.names]
        elif isinstance(n, ast.ImportFrom):
            mods = [n.module or ""]
            if any(a.name == "ImageFont" for a in n.names):
                errs.append(f"{p.name}:{n.lineno}: ImageFont import")
        for m in mods:
            if m.split(".")[0] not in ALLOWED_MODULES or "ImageFont" in m:
                errs.append(f"{p.name}:{n.lineno}: import {m}")
        if isinstance(n, ast.Attribute) and n.attr in ("ImageFont", "truetype", "load_default"):
            errs.append(f"{p.name}:{n.lineno}: {n.attr}")
        if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id in ("exec", "eval", "__import__"):
            errs.append(f"{p.name}:{n.lineno}: {n.func.id}")
        if isinstance(n, ast.Constant) and isinstance(n.value, str) and BAD_STR.search(n.value) and len(n.value) < 200:
            errs.append(f"{p.name}:{n.lineno}: string {n.value[:60]!r}")
    for tok in tokenize.generate_tokens(io.StringIO(src).readline):
        if tok.type == tokenize.NAME and tok.string in ("ImageFont", "urllib", "requests"):
            errs.append(f"{p.name}:{tok.start[0]}: token {tok.string}")

for key in sorted(READ_ALLOW):
    if key not in seen_reads:
        errs.append(f"READ_ALLOW entry {key} is stale: no generator makes that read any more - drop it, so the "
                    f"allow-list cannot grow into a blanket exemption")
# the rule is live: a generator that opens art the project did not generate must fail it
_probe = 'from pathlib import Path\nImage.open(Path("ref" + "erence/plates/hud.png"))\n'
if not read_errors("gen_probe.py", _probe)[0]:
    errs.append("the file-read rule accepts a generator that opens a plate outside our own art; it is not enforcing")
if read_errors("uiart.py", _probe)[0]:
    errs.append("the file-read rule is being applied to the exempt toolkit; READ_EXEMPT_FILES has stopped working")

# check_art.py holds the art gates as constants: it must never read an image or file itself, only generated
# sprites through uiart helpers and generator previews.
art_path = TOOLS / "tests" / "check_art.py"
art_src = art_path.read_text(encoding="utf-8")
for n in ast.walk(ast.parse(art_src)):
    if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute) and n.func.attr in ("open", "read_bytes", "read_text", "load"):
        errs.append(f"check_art.py:{n.lineno}: direct file read .{n.func.attr}")
    if isinstance(n, ast.Constant) and isinstance(n.value, str) and BAD_STR.search(n.value) and len(n.value) < 200:
        errs.append(f"check_art.py:{n.lineno}: string {n.value[:60]!r}")

names: list[str] = []
for jp in UI.rglob("*.json"):
    names += list(json.loads(jp.read_text(encoding="utf-8")).get("sprites", {}))
    names.append(jp.name)
names += [p.name for p in UI.rglob("*.png")]
tokens = json.loads((TOOLS / "tokens.json").read_text(encoding="utf-8"))
names += list(tokens) if isinstance(tokens, dict) else []
for n in names:
    if BAD_NAME.search(n) or re.search(r"zen", n, re.I):
        errs.append(f"source-game name: {n}")
for e in errs:
    print("FAIL", e)
print(f"sources: {len(errs)} problems, {len(names)} names scanned")
sys.exit(1 if errs else 0)
