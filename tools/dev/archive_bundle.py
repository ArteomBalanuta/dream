"""Verify and pack a Dream client bundle (stdlib only; run with `python -I`).

  python -I tools/dev/archive_bundle.py verify --rid win-x64|linux-x64 PATH   (PATH = stage folder, .zip or .tar.gz)
  python -I tools/dev/archive_bundle.py pack --rid R --stage DIR --out ARCHIVE (.zip for win, .tar.gz for linux)
  python -I tools/dev/archive_bundle.py --selftest

The archive has DIR's name as its single top-level folder. In a tar.gz, `dream` and `run-dream.sh` are 0755, the rest
0644, and run-dream.sh is written with LF line endings.
"""
import argparse
import fnmatch
import io
import os
import sys
import tarfile
import tempfile
import zipfile

REQUIRED = {
    "win-x64": ["dream.exe", "libbulletc.dll", "vcomp140.dll", "cimgui.dll", "glfw3.dll", "e_sqlite3.dll",
                "OpenTK.Audio.OpenAL.dll", "README.txt", "config/content/*.json", "shaders/*"],
    "linux-x64": ["dream", "libbulletc.so", "libcimgui.so", "libglfw.so.3", "libe_sqlite3.so", "run-dream.sh",
                  "README.txt", "config/content/*.json", "shaders/*"],
}
FORBIDDEN = ["*player-profiles.db*", "account-tokens.json", "imgui.ini"]
# native files of the other platform at the bundle root (managed .dll files are fine in a linux bundle)
FOREIGN = {"win-x64": ["*.so", "*.so.*", "*.dylib"], "linux-x64": ["*.exe", "libbulletc.dll", "cimgui.dll",
           "glfw3.dll", "e_sqlite3.dll", "vcomp140.dll", "OpenAL32.dll", "soft_oal.dll"]}
EXEC_FILES = ("dream", "run-dream.sh")


def _entries_dir(path):
    out = []
    for dp, _dn, fns in os.walk(path):
        for fn in fns:
            out.append((os.path.relpath(os.path.join(dp, fn), path).replace(os.sep, "/"), None))
    return out


def _entries_archive(path):
    raw, problems = [], []
    if path.endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            for i in z.infolist():
                if not i.is_dir():
                    raw.append((i.filename, None))
    else:
        with tarfile.open(path, "r:gz") as t:
            for m in t.getmembers():
                if m.isfile():
                    raw.append((m.name, m.mode & 0o7777))
    tops = {n.split("/", 1)[0] for n, _ in raw}
    if len(tops) != 1:
        problems.append("archive must have exactly one top-level folder, found %s" % sorted(tops))
    return [(n.split("/", 1)[1] if "/" in n else n, m) for n, m in raw], problems


def verify_entries(rid, entries, check_modes):
    problems = []
    names = [n for n, _ in entries]
    for pat in REQUIRED[rid]:
        if not any(fnmatch.fnmatchcase(n, pat) for n in names):
            problems.append("missing: " + pat)
    for n in names:
        leaf = n.rsplit("/", 1)[-1]
        for pat in FORBIDDEN:
            if fnmatch.fnmatchcase(leaf, pat):
                problems.append("forbidden file: " + n)
        if "/" not in n:
            for pat in FOREIGN[rid]:
                if fnmatch.fnmatchcase(n, pat):
                    problems.append("other platform's file: " + n)
    if check_modes and rid == "linux-x64":
        modes = dict(entries)
        for f in EXEC_FILES:
            if f in modes and not (modes[f] or 0) & 0o111:
                problems.append("not executable in tar: " + f)
    return problems


def verify(rid, path):
    if os.path.isdir(path):
        entries, problems, modes = _entries_dir(path), [], False
    else:
        entries, problems = _entries_archive(path)
        modes = path.endswith(".tar.gz")
    return problems + verify_entries(rid, entries, modes)


def pack(rid, stage, out):
    stage = os.path.abspath(stage.rstrip("/\\"))
    top = os.path.basename(stage)
    files = []
    for dp, dn, fns in os.walk(stage):
        dn.sort()
        for fn in sorted(fns):
            full = os.path.join(dp, fn)
            files.append((full, top + "/" + os.path.relpath(full, stage).replace(os.sep, "/")))
    if rid == "win-x64":
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for full, arc in files:
                z.write(full, arc)
        return
    with tarfile.open(out, "w:gz", compresslevel=6) as t:
        for full, arc in files:
            ti = t.gettarinfo(full, arc)
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            name = arc.rsplit("/", 1)[-1]
            ti.mode = 0o755 if name in EXEC_FILES and arc.count("/") == 1 else 0o644
            if name == "run-dream.sh":
                data = open(full, "rb").read().replace(b"\r\n", b"\n")
                ti.size = len(data)
                t.addfile(ti, io.BytesIO(data))
            else:
                with open(full, "rb") as fh:
                    t.addfile(ti, fh)


def _touch_stage(root, rid, skip=(), extra=()):
    for f in REQUIRED[rid]:
        if f in skip:
            continue
        f = f.replace("*.json", "a.json").replace("shaders/*", "shaders/a.glsl")
        p = os.path.join(root, *f.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as fh:
            fh.write(b"#!/bin/sh\r\nexec ./dream\r\n" if f == "run-dream.sh" else b"x")
    for f in extra:
        p = os.path.join(root, *f.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as fh:
            fh.write(b"x")


def selftest():
    fails = []

    def expect(label, got, want_fail):
        if bool(got) != want_fail:
            fails.append("%s: expected %s, got %s" % (label, "failure" if want_fail else "pass", got))

    with tempfile.TemporaryDirectory() as tmp:
        def make(rid, name, **kw):
            d = os.path.join(tmp, name, "dream-t-" + rid)
            _touch_stage(d, rid, **kw)
            return d

        for rid in REQUIRED:
            expect(rid + " good dir", verify(rid, make(rid, "g-" + rid)), False)
        expect("missing libbulletc.so", verify("linux-x64", make("linux-x64", "n1", skip=("libbulletc.so",))), True)
        expect("missing libbulletc.dll", verify("win-x64", make("win-x64", "n2", skip=("libbulletc.dll",))), True)
        expect("missing vcomp140", verify("win-x64", make("win-x64", "n3", skip=("vcomp140.dll",))), True)
        expect("stray db", verify("linux-x64", make("linux-x64", "n4", extra=("config/player-profiles.db-wal",))), True)
        expect("stray imgui.ini", verify("win-x64", make("win-x64", "n5", extra=("imgui.ini",))), True)
        expect("foreign native", verify("linux-x64", make("linux-x64", "n6", extra=("glfw3.dll",))), True)
        expect("win dll in linux stage", verify("linux-x64", make("linux-x64", "n7", extra=("libbulletc.dll",))), True)
        expect("linux so in win stage", verify("win-x64", make("win-x64", "n8", extra=("libbulletc.so",))), True)
        expect("versioned so in win stage", verify("win-x64", make("win-x64", "n9", extra=("libglfw.so.3",))), True)
        expect("account tokens", verify("linux-x64", make("linux-x64", "n10", extra=("account-tokens.json",))), True)
        expect("nested account tokens", verify("win-x64", make("win-x64", "n11", extra=("config/account-tokens.json",))), True)
        expect("nested imgui.ini", verify("win-x64", make("win-x64", "n12", extra=("config/imgui.ini",))), True)
        expect("nested db-wal", verify("linux-x64", make("linux-x64", "n13", extra=("a/b/player-profiles.db-wal",))), True)
        expect("nested foreign so allowed", verify("win-x64", make("win-x64", "n14", extra=("sub/x.so",))), False)
        expect("managed dll allowed in linux", verify("linux-x64", make("linux-x64", "n15", extra=("Foo.dll",))), False)
        t = os.path.join(tmp, "ok.tar.gz")
        pack("linux-x64", make("linux-x64", "a1"), t)
        expect("good tar", verify("linux-x64", t), False)
        with tarfile.open(t) as tf:
            modes = {m.name.split("/", 1)[1]: m.mode for m in tf.getmembers() if m.isfile()}
            data = tf.extractfile("dream-t-linux-x64/run-dream.sh").read()
        if modes["dream"] != 0o755 or modes["run-dream.sh"] != 0o755 or modes["libbulletc.so"] != 0o644:
            fails.append("pack modes wrong: %s" % modes)
        if b"\r" in data:
            fails.append("run-dream.sh kept CRLF")
        bad = os.path.join(tmp, "bad.tar.gz")
        with tarfile.open(t) as src, tarfile.open(bad, "w:gz") as dst:
            for m in src.getmembers():
                if m.isfile():
                    if m.name.endswith("/dream"):
                        m.mode = 0o644
                    dst.addfile(m, src.extractfile(m))
        expect("tar without exec bit", verify("linux-x64", bad), True)
        z = os.path.join(tmp, "ok.zip")
        pack("win-x64", make("win-x64", "a2"), z)
        expect("good zip", verify("win-x64", z), False)
        two = os.path.join(tmp, "two.zip")
        with zipfile.ZipFile(z) as s, zipfile.ZipFile(two, "w") as o:
            for i in s.infolist():
                nm = i.filename.replace("dream-t-win-x64/", "x/", 1) if i.filename.endswith("dream.exe") else i.filename
                o.writestr(nm, s.read(i))
        expect("two top-level folders", verify("win-x64", two), True)
        flat = os.path.join(tmp, "flat.zip")
        with zipfile.ZipFile(z) as s, zipfile.ZipFile(flat, "w") as o:
            for i in s.infolist():
                o.writestr(i.filename.split("/", 1)[1], s.read(i))
        expect("zip with no top-level folder", verify("win-x64", flat), True)
        expect("win zip verified as linux", verify("linux-x64", z), True)
    for f in fails:
        print("SELFTEST FAIL: " + f)
    print("selftest: %s" % ("FAILED" if fails else "ok"))
    return 1 if fails else 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--selftest", action="store_true")
    sub = ap.add_subparsers(dest="cmd")
    v = sub.add_parser("verify")
    v.add_argument("--rid", required=True, choices=list(REQUIRED))
    v.add_argument("path")
    p = sub.add_parser("pack")
    p.add_argument("--rid", required=True, choices=list(REQUIRED))
    p.add_argument("--stage", required=True)
    p.add_argument("--out", required=True)
    a = ap.parse_args()
    if a.selftest:
        return selftest()
    if a.cmd == "verify":
        probs = verify(a.rid, a.path)
        for x in probs:
            print("verify FAIL: " + x)
        print("verify %s: %s" % (a.rid, "FAILED (%d)" % len(probs) if probs else "ok"))
        return 1 if probs else 0
    if a.cmd == "pack":
        pack(a.rid, a.stage, a.out)
        print("%s  %.1f MB" % (a.out, os.path.getsize(a.out) / 1048576.0))
        return 0
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
