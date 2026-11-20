#!/usr/bin/env python3
"""Embed a .glb into the turnaround viewer page.
  python make_viewer.py out/GreyHare.glb out/GreyHare_viewer.html --title "Grey Hare"
Serve the folder (python -m http.server) and open the page; file:// may be blocked."""
import base64
import os
import sys

glb, html = sys.argv[1], sys.argv[2]
title = sys.argv[sys.argv.index("--title") + 1] if "--title" in sys.argv else os.path.splitext(os.path.basename(glb))[0]
tpl = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "viewer_template.html")).read()
b64 = base64.b64encode(open(glb, "rb").read()).decode()
open(html, "w").write(tpl.replace("__GLB_B64__", b64).replace("__TITLE__", title))
print(f"wrote {html} ({os.path.getsize(html)/1e6:.1f} MB)")
