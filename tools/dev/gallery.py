"""Static HTML gallery of dream's generated art and capture screenshots (stdlib only).

    python -I tools/dev/gallery.py            # writes artifacts/gallery/index.html
    python -I tools/dev/gallery.py --root R   # another repo root
    python -I tools/dev/gallery.py --selftest

Groups: ui-assets previews, dream/ui sheets, iso and terrain art, newest captures. Images are linked
by relative path (the browser scales them to thumbnails), so open index.html from where it was written.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import html
import os
import sys
import tempfile
import urllib.parse
from pathlib import Path

NEWEST_CAPTURES = 40

# (title, directory relative to root, glob, newest-first limit or None)
GROUPS = (
    ("ui-assets previews", "tools/ui-assets/previews", "*.png", None),
    ("dream/ui sheets", "dream/ui", "**/*.png", None),
    ("iso art", "tools/ui-assets/iso", "**/*.png", None),
    ("terrain art", "tools/ui-assets/terrain", "**/*.png", None),
    ("newest captures", "artifacts/captures", "*/*.png", NEWEST_CAPTURES),
)


def collect(root: Path) -> list[tuple[str, list[Path]]]:
    """Return [(title, files)] for every group, files sorted by name (captures: newest first)."""
    result = []
    for title, rel, pattern, limit in GROUPS:
        base = root / rel
        files = [p for p in base.glob(pattern) if p.is_file()] if base.is_dir() else []
        if limit is not None:
            files.sort(key=lambda p: p.stat().st_mtime, reverse=True)
            files = files[:limit]
        else:
            files.sort(key=lambda p: p.as_posix().lower())
        result.append((title, files))
    return result


def _link(out_dir: Path, target: Path) -> str:
    rel = os.path.relpath(target, out_dir).replace(os.sep, "/")
    return urllib.parse.quote(rel, safe="/")


def render(root: Path, out_dir: Path, groups: list[tuple[str, list[Path]]]) -> str:
    parts = [
        "<!doctype html><meta charset=utf-8><title>dream gallery</title>",
        "<style>body{background:#111;color:#ccc;font:13px sans-serif;margin:16px}"
        "h2{border-bottom:1px solid #444;padding-bottom:4px}"
        ".g{display:flex;flex-wrap:wrap;gap:10px}"
        "figure{margin:0;width:200px}figure img{width:200px;height:150px;object-fit:contain;"
        "background:#222;image-rendering:pixelated}"
        "figcaption{word-break:break-all}small{color:#888}a{color:#9ab}</style>",
        f"<h1>dream gallery</h1><p><small>generated {html.escape(_dt.datetime.now().isoformat(timespec='seconds'))}</small></p>",
    ]
    for title, files in groups:
        parts.append(f'<h2 id="{html.escape(title.replace(" ", "-"))}">{html.escape(title)} ({len(files)})</h2><div class="g">')
        for path in files:
            href = _link(out_dir, path)
            name = path.relative_to(root).as_posix() if path.is_relative_to(root) else path.name
            mtime = _dt.datetime.fromtimestamp(path.stat().st_mtime).strftime("%Y-%m-%d %H:%M")
            parts.append(
                f'<figure><a href="{href}"><img loading="lazy" src="{href}" alt="{html.escape(path.name)}"></a>'
                f"<figcaption>{html.escape(name)}<br><small>{mtime}</small></figcaption></figure>"
            )
        if not files:
            parts.append("<p><small>nothing here</small></p>")
        parts.append("</div>")
    return "\n".join(parts) + "\n"


def build(root: Path, out_dir: Path | None = None) -> Path:
    out_dir = out_dir or root / "artifacts" / "gallery"
    out_dir.mkdir(parents=True, exist_ok=True)
    index = out_dir / "index.html"
    index.write_text(render(root, out_dir, collect(root)), encoding="utf-8")
    return index


def selftest() -> int:
    failures = []

    def check(cond: bool, message: str) -> None:
        if not cond:
            failures.append(message)

    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        png = b"\x89PNG\r\n\x1a\n"
        wanted = [
            "tools/ui-assets/previews/hud_sheet.png",
            "dream/ui/hud/hud.png",
            "dream/ui/items/items.png",
            "tools/ui-assets/iso/iso_sheet.png",
            "tools/ui-assets/terrain/grass top.png",
        ]
        for rel in wanted:
            f = root / rel
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_bytes(png)
        # 45 captures with increasing mtimes: only the newest 40 may appear.
        for i in range(45):
            f = root / "artifacts" / "captures" / f"run{i % 3}" / f"shot{i:02d}.png"
            f.parent.mkdir(parents=True, exist_ok=True)
            f.write_bytes(png)
            os.utime(f, (1_700_000_000 + i, 1_700_000_000 + i))
        # Negatives: things that must NOT be listed.
        (root / "dream/ui/hud/readme.txt").write_text("x")
        (root / "artifacts/captures/loose.png").write_bytes(png)  # not in a run folder

        index = build(root)
        text = index.read_text(encoding="utf-8")
        groups = dict(collect(root))

        check(index == root / "artifacts" / "gallery" / "index.html", "index path")
        check(len(groups["ui-assets previews"]) == 1, "previews group")
        check(len(groups["dream/ui sheets"]) == 2, "ui sheets group")
        check(len(groups["iso art"]) == 1 and len(groups["terrain art"]) == 1, "iso/terrain groups")
        caps = groups["newest captures"]
        check(len(caps) == NEWEST_CAPTURES, f"capture limit: {len(caps)}")
        check(all(p.name != "loose.png" for p in caps), "loose capture excluded")
        check(not any(f"shot0{i}.png" in text for i in range(5)), "oldest 5 captures are cut")
        check("shot44.png" in text, "newest capture listed")
        check('href="../../tools/ui-assets/previews/hud_sheet.png"' in text, "relative link")
        check("grass%20top.png" in text, "link is url-quoted")
        check("readme.txt" not in text, "non-png excluded")
        for title, _, _, _ in GROUPS:
            check(f"{title} (" in text, f"heading {title}")
        # Every linked file must exist relative to the index.
        for href in {h.split('"')[0] for h in text.split('href="')[1:]}:
            target = (index.parent / urllib.parse.unquote(href)).resolve()
            check(target.exists(), f"dead link {href}")
        # An empty tree still renders every group.
        with tempfile.TemporaryDirectory() as empty:
            empty_text = build(Path(empty)).read_text(encoding="utf-8")
            check(empty_text.count("nothing here") == len(GROUPS), "empty tree shows every group empty")

    for message in failures:
        print("FAIL", message)
    print("gallery selftest:", "FAILED" if failures else "ok")
    return 1 if failures else 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--selftest", action="store_true")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args(argv)
    if args.selftest:
        return selftest()
    index = build(args.root.resolve())
    print(index)
    return 0


if __name__ == "__main__":
    sys.exit(main())
