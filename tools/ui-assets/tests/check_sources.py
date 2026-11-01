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
                   "forge", "gemtrace", "concurrent", "os"}
BAD_STR = re.compile(r"reference|\.webp|fonts|\.ttf|\.otf|https?:|screenshot", re.I)
BAD_NAME = re.compile(r"(^|[._\-])(mu|muonline|zen|chaos|larkan|loch|arcana|soul|bless|webzen|kundun|lorencia)([._\-]|$)", re.I)
errs: list[str] = []

for p in sorted(TOOLS.glob("*.py")):
    src = p.read_text(encoding="utf-8")
    tree = ast.parse(src)
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
        # every file read must be of generated output (out_root) or a sibling tool file
        if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute) and n.func.attr in ("open", "read_bytes", "read_text"):
            seg = ast.get_source_segment(src, n) or ""
            if p.name != "uiart.py":
                print(f"note {p.name}:{n.lineno}: read {seg[:70]!r}")
    for tok in tokenize.generate_tokens(io.StringIO(src).readline):
        if tok.type == tokenize.NAME and tok.string in ("ImageFont", "urllib", "requests"):
            errs.append(f"{p.name}:{tok.start[0]}: token {tok.string}")

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
